using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DotnetSpider.Portal.BackgroundService;
using DotnetSpider.Portal.Common;
using DotnetSpider.Portal.Data;
using DotnetSpider.Portal.Models.Spider;
using DotnetSpider.Portal.Models.SpiderContainer;
using DotnetSpider.Statistics.Store;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Quartz;
using X.PagedList;

namespace DotnetSpider.Portal.Controllers.API
{
	[ApiController]
	[Route("api/v1.0/spiders")]
	public class SpiderController : Controller
	{
		private readonly ILogger _logger;
		private readonly PortalDbContext _dbContext;
		private readonly IScheduler _sched;

		public SpiderController(PortalDbContext dbContext,
			IScheduler sched,
			ILogger<SpiderController> logger)
		{
			_logger = logger;
			_dbContext = dbContext;
			_sched = sched;
		}

		[HttpGet("spider/{id}")]
		public async Task<IActionResult> Edit(int id)
		{
			var viewModel = new ViewSpiderModel();
			var spider = await _dbContext.Spiders.FirstOrDefaultAsync(x => x.Id == id);
			if (spider == null)
			{
				return NotFound();
			}

			viewModel.Cron = spider.Cron;
			viewModel.Environment = spider.Environment;
			viewModel.Name = spider.Name;
			viewModel.Registry = spider.Registry;
			viewModel.Repository = spider.Repository;
			viewModel.Type = spider.Type;
			viewModel.Tag = spider.Tag;
			viewModel.Id = id;

			var dockerRepository = await _dbContext.DockerRepositories.FirstOrDefaultAsync(x =>
				x.Registry == spider.Registry && x.Repository == spider.Repository);

			if (dockerRepository == null)
			{
				return NotFound();
			}

			if (!string.IsNullOrWhiteSpace(viewModel.Registry))
			{
				viewModel.Tags = await GetRepositoryTagsAsync(dockerRepository.Schema, dockerRepository.Registry,
					dockerRepository.Repository,
					dockerRepository.UserName, dockerRepository.Password);
			}

			return View(viewModel);
		}

		[HttpPost("spider/{spiderId}")]
		public async Task<IActionResult> UpdateAsync(int spiderId, ViewSpiderModel viewModel)
		{
			// POST 过来的镜像仓库是选择出来的，如果不正确表明可能是其它 HACK 过来的数据, 直接抛 404即可
			var dockerRepository = await _dbContext.DockerRepositories.FirstOrDefaultAsync(x =>
				x.Registry == viewModel.Registry && x.Repository == viewModel.Repository);
			if (dockerRepository == null)
			{
				return NotFound();
			}

			if (ModelState.IsValid)
			{
				var spider = await _dbContext.Spiders.FirstOrDefaultAsync(x => x.Id == spiderId);
				if (spider == null)
				{
					return NotFound();
				}

				var exists = await _dbContext.Spiders.AnyAsync(x =>
					x.Name == viewModel.Name && x.Id != spiderId);
				if (exists)
				{
					ModelState.AddModelError("Name", "名称已经存在");
				}

				try
				{
					TriggerBuilder.Create().WithCronSchedule(viewModel.Cron).Build();
				}
				catch
				{
					ModelState.AddModelError("Cron", "Cron 表达式不正确");
				}

				if (ModelState.IsValid)
				{
					var transaction = await _dbContext.Database.BeginTransactionAsync();
					try
					{
						var reSched = viewModel.Cron == spider.Cron;
						spider.Name = viewModel.Name;
						spider.Cron = viewModel.Cron;
						spider.Type = viewModel.Type;
						spider.Registry = dockerRepository.Registry;
						spider.Repository = viewModel.Repository;
						spider.Environment = viewModel.Environment;
						spider.Tag = viewModel.Tag;
						spider.LastModificationTime = DateTimeOffset.Now;

						await _dbContext.SaveChangesAsync();

						if (reSched)
						{
							var id = spider.Id.ToString();
							var deleted = await _sched.DeleteJob(new JobKey(id));
							if (!deleted)
							{
								throw new Exception("删除定时任务失败");
							}

							await ScheduleJobAsync(spider.Cron, id, spider.Name);
						}

						transaction.Commit();

						return Redirect("/spider");
					}
					catch (Exception e)
					{
						_logger.LogError($"更新任务失败: {e}");
						try
						{
							transaction.Rollback();
						}
						catch (Exception re)
						{
							_logger.LogError($"回滚更新任务失败: {re}");
						}
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(viewModel.Registry))
			{
				viewModel.Tags = await GetRepositoryTagsAsync(dockerRepository.Schema, dockerRepository.Registry,
					dockerRepository.Repository,
					dockerRepository.UserName, dockerRepository.Password);
			}

			return View("Edit", viewModel);
		}

		[HttpGet("spider/create")]
		public async Task<IActionResult> Create(string registry, string repository)
		{
			var viewModel = new CreateSpiderViewModel();

			registry = registry?.Trim();
			repository = repository?.Trim();

			var dockerRepository = await _dbContext.DockerRepositories.FirstOrDefaultAsync(x =>
				x.Registry == registry && x.Repository == repository);

			if (dockerRepository == null)
			{
				return NotFound();
			}

			if (!string.IsNullOrWhiteSpace(dockerRepository.Registry))
			{
				viewModel.Tags = await GetRepositoryTagsAsync(dockerRepository.Schema, dockerRepository.Registry,
					dockerRepository.Repository,
					dockerRepository.UserName, dockerRepository.Password);
			}

			return View(viewModel);
		}


		[HttpPost("spider")]
		public async Task<IActionResult> CreateAsync(CreateSpiderViewModel viewModel)
		{
			// POST 过来的镜像仓库是选择出来的，如果不正确表明可能是其它 HACK 过来的数据, 直接抛 404即可
			var dockerRepository = await _dbContext.DockerRepositories.FirstOrDefaultAsync(x =>
				x.Registry == viewModel.Registry && x.Repository == viewModel.Repository);
			if (dockerRepository == null)
			{
				return NotFound();
			}

			if (ModelState.IsValid)
			{
				var exists = await _dbContext.Spiders.AnyAsync(x =>
					x.Name == viewModel.Name);
				if (exists)
				{
					ModelState.AddModelError("Name", "名称已经存在");
				}

				try
				{
					TriggerBuilder.Create().WithCronSchedule(viewModel.Cron).Build();
				}
				catch
				{
					ModelState.AddModelError("Cron", "Cron 表达式不正确");
				}

				if (ModelState.IsValid)
				{
					var transaction = await _dbContext.Database.BeginTransactionAsync();
					try
					{
						var spider = new Data.Spider
						{
							Name = viewModel.Name,
							Cron = viewModel.Cron,
							Type = viewModel.Type,
							Registry = dockerRepository.Registry,
							Repository = viewModel.Repository,
							Environment = viewModel.Environment,
							Tag = viewModel.Tag,
							CreationTime = DateTimeOffset.Now
						};
						await _dbContext.Spiders.AddAsync(spider);
						await _dbContext.SaveChangesAsync();

						var id = spider.Id.ToString();
						await ScheduleJobAsync(viewModel.Cron, id, spider.Name);

						transaction.Commit();

						return Redirect("/spider");
					}
					catch (Exception e)
					{
						_logger.LogError($"添加任务失败: {e}");
						try
						{
							transaction.Rollback();
						}
						catch (Exception re)
						{
							_logger.LogError($"回滚添加任务失败: {re}");
						}
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(viewModel.Registry))
			{
				viewModel.Tags = await GetRepositoryTagsAsync(dockerRepository.Schema, dockerRepository.Registry,
					dockerRepository.Repository,
					dockerRepository.UserName, dockerRepository.Password);
			}

			return View("Create", viewModel);
		}

		[HttpGet("spider")]
		public async Task<IActionResult> Retrieve(string q, int page, int size)
		{
			page = page <= 1 ? 1 : page;
			size = size <= 20 ? 20 : size;
			IPagedList<Data.Spider> viewModel;
			if (string.IsNullOrWhiteSpace(q))
			{
				viewModel = await _dbContext.Spiders.OrderByDescending(x => x.Id)
					.ToPagedListAsync(page, size);
			}
			else
			{
				viewModel = await _dbContext.Spiders.Where(x => x.Name.Contains(q))
					.OrderByDescending(x => x.Id)
					.ToPagedListAsync(page, size);
			}

			return View(viewModel);
		}

		[HttpDelete("spider/{id}")]
		public async Task<IActionResult> DeleteAsync(int id)
		{
			var item = await _dbContext.Spiders.FirstOrDefaultAsync(x => x.Id == id);
			if (item != null)
			{
				_dbContext.Spiders.Remove(item);
				await _dbContext.SaveChangesAsync();
			}

			return NotFound();
		}

		[HttpPost("spider/{id}/run")]
		public async Task<IActionResult> RunAsync(int id)
		{
			try
			{
				await _sched.TriggerJob(new JobKey(id.ToString()));
				return Ok();
			}
			catch (Exception e)
			{
				_logger.LogError($"启动失败: {e}");
				return StatusCode((int)HttpStatusCode.InternalServerError, new {e.Message});
			}
		}

		[HttpPost("spider/{id}/disable")]
		public async Task<IActionResult> DisableAsync(int id)
		{
			try
			{
				var item = await _dbContext.Spiders.FirstOrDefaultAsync(x => x.Id == id);
				if (item != null && item.Enable)
				{
					item.Enable = false;
					_dbContext.Spiders.Update(item);
					await _dbContext.SaveChangesAsync();
				}

				return Ok();
			}
			catch (Exception e)
			{
				_logger.LogError($"禁用失败: {e}");
				return StatusCode((int)HttpStatusCode.InternalServerError, new {e.Message});
			}
		}

		[HttpPost("spider/{id}/enable")]
		public async Task<IActionResult> EnableAsync(int id)
		{
			try
			{
				var item = await _dbContext.Spiders.FirstOrDefaultAsync(x => x.Id == id);
				if (item != null && !item.Enable)
				{
					item.Enable = true;
					_dbContext.Spiders.Update(item);
					await _dbContext.SaveChangesAsync();
				}

				return Ok();
			}
			catch (Exception e)
			{
				_logger.LogError($"启用失败: {e}");
				return StatusCode((int)HttpStatusCode.InternalServerError, new {e.Message});
			}
		}

		[HttpGet("{id}/containers")]
		public async Task<IActionResult> Retrieve(int id, int page, int size)
		{
			page = page <= 1 ? 1 : page;
			size = size <= 20 ? 20 : size;

			var containers = await _dbContext.SpiderContainers.Where(x => x.SpiderId == id)
				.OrderByDescending(x => x.Id)
				.ToPagedListAsync(page, size);

			var batches = await containers.Select(x => x.Batch).ToListAsync();
			var dict = await _dbContext.Set<SpiderStatistics>().Where(x => batches.Contains(x.Id))
				.ToDictionaryAsync(x => x.Id, x => x);

			var list = new List<ListSpiderContainerViewModel>();
			foreach (var container in containers)
			{
				var item = new ListSpiderContainerViewModel
				{
					Batch = container.Batch,
					ContainerId = container.ContainerId,
					SpiderId = container.SpiderId,
					Status = container.Status,
					CreationTime = container.CreationTime
				};
				if (dict.ContainsKey(item.Batch))
				{
					item.Total = dict[item.Batch].Total;
					item.Failed = dict[item.Batch].Failure;
					item.Success = dict[item.Batch].Success;
					item.Start = dict[item.Batch].Start;
					item.Exit = dict[item.Batch].Exit;
					item.Left = item.Total - item.Success;
				}

				list.Add(item);
			}

			return View(new StaticPagedList<ListSpiderContainerViewModel>(list, page, size,
				containers.GetMetaData().TotalItemCount));
		}

		[HttpPost("{id}/exit")]
		public async Task<IApiResult> ExitAsync(string id)
		{
			try
			{
				await _mq.PublishAsBytesAsync(string.Format(TopicNames.Spider, id.ToUpper()), new Exit {Id = id});
				return new ApiResult("OK");
			}
			catch (Exception e)
			{
				_logger.LogError($"关闭失败: {e}");
				return new FailedResult(e.Message);
			}
		}

		private async Task<List<string>> GetRepositoryTagsAsync(string schema, string registry, string repository,
			string user,
			string password)
		{
			try
			{
				registry = $"{schema}://{registry}";
				var httpClient = HttpClientFactory.GetHttpClient(registry,
					user, password);
				var json = await httpClient.GetStringAsync(
					$"{registry}/v2/{repository}/tags/list");
				var repositoryTags = JsonConvert.DeserializeObject<RepositoryTags>(json);
				repositoryTags.Tags.Remove("latest");
				var list = new List<string> {"latest"};
				repositoryTags.Tags.Sort(new StringCompare());
				list.AddRange(repositoryTags.Tags);
				return list.Take(20).ToList();
			}
			catch (Exception e)
			{
				_logger.LogError(e.ToString());
				return new List<string>();
			}
		}

		private async Task ScheduleJobAsync(string cron, string id, string name)
		{
			var trigger = TriggerBuilder.Create().WithCronSchedule(cron).WithIdentity(id)
				.Build();
			var qzJob = JobBuilder.Create<QuartzJob>().WithIdentity(id).WithDescription(name)
				.RequestRecovery(true).Build();
			await _sched.ScheduleJob(qzJob, trigger);
		}

		class StringCompare : IComparer<string>
		{
			public int Compare(string x, string y)
			{
				var result = String.CompareOrdinal(x, y);
				if (result == 0)
				{
					return 0;
				}
				else if (result > 0)
				{
					return 0;
				}
				else
				{
					return 1;
				}
			}
		}
	}
}
