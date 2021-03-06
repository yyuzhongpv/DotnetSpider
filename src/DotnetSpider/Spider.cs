using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Agent;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Storage;
using DotnetSpider.Extensions;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.RequestSupplier;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SwiftMQ;

[assembly: InternalsVisibleTo("DotnetSpider.Tests")]

namespace DotnetSpider
{
	public abstract partial class Spider :
		BackgroundService
	{
		private readonly List<IDataFlow> _dataFlows;
		private readonly List<IRequestSupplier> _requestSuppliers;
		private readonly SpiderOptions _options;
		private readonly RequestedQueue _requestedQueue;
		private AsyncMessageConsumer<byte[]> _consumer;
		private readonly SpiderServices _services;

		/// <summary>
		/// 爬虫标识
		/// </summary>
		protected string Id { get; private set; }

		/// <summary>
		/// 爬虫名称
		/// </summary>
		protected string Name { get; private set; }

		protected readonly ILogger Logger;

		protected Spider(IOptions<SpiderOptions> options,
			SpiderServices services,
			ILogger<Spider> logger
		)
		{
			Logger = logger;
			_services = services;
			_options = options.Value;
			_requestedQueue = new RequestedQueue(_options);
			_requestSuppliers = new List<IRequestSupplier>();
			_dataFlows = new List<IDataFlow>();
		}

		/// <summary>
		/// 初始化爬虫数据
		/// </summary>
		/// <param name="stoppingToken"></param>
		/// <returns></returns>
		protected abstract Task InitializeAsync(CancellationToken stoppingToken);

		/// <summary>
		/// 获取爬虫标识和名称
		/// </summary>
		/// <returns></returns>
		protected abstract (string Id, string Name) GetIdAndName();

		protected IDataFlow GetDefaultStorage()
		{
			if (string.IsNullOrWhiteSpace(_options.Storage))
			{
				throw new ArgumentNullException($"Storage is not configured");
			}

			var type = Type.GetType(_options.Storage);
			if (type == null)
			{
				throw new SpiderException($"Type of storage {_options.Storage} not found");
			}

			if (!typeof(StorageBase).IsAssignableFrom(type) && !typeof(EntityStorageBase).IsAssignableFrom(type))
			{
				throw new SpiderException($"{type} is not a storage dataFlow");
			}

			var method = type.GetMethod("CreateFromOptions");

			if (method == null)
			{
				throw new SpiderException($"Storage {type} didn't implement method CreateFromOptions");
			}

			var storage = method.Invoke(null, new object[] {_options});
			if (storage == null)
			{
				throw new SpiderException("Create default storage failed");
			}

			return (IDataFlow)storage;
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			Logger.LogInformation($"{Id}, {Name} stopping");
			_consumer?.Close();

			await base.StopAsync(cancellationToken);

			foreach (var dataFlow in _dataFlows)
			{
				dataFlow.Dispose();
			}

			Logger.LogInformation($"{Id}, {Name} stopped");
		}

		protected virtual void ConfigureRequest(Request request)
		{
		}

		protected virtual void AddRequestSupplier(IRequestSupplier requestSupplier)
		{
			requestSupplier.NotNull(nameof(requestSupplier));
			_requestSuppliers.Add(requestSupplier);
		}

		protected virtual Spider AddDataFlow(IDataFlow dataFlow)
		{
			dataFlow.NotNull(nameof(dataFlow));
			_dataFlows.Add(dataFlow);
			return this;
		}

		protected async Task<int> AddRequestsAsync(params string[] requests)
		{
			if (requests == null || requests.Length == 0)
			{
				return 0;
			}

			return await AddRequestsAsync(requests.Select(x => new Request(x)));
		}

		protected async Task<int> AddRequestsAsync(params Request[] requests)
		{
			if (requests == null || requests.Length == 0)
			{
				return 0;
			}

			return await AddRequestsAsync((IEnumerable<Request>)requests);
		}

		protected async Task<int> AddRequestsAsync(IEnumerable<Request> requests)
		{
			if (requests == null)
			{
				return 0;
			}

			var list = new List<Request>();

			foreach (var request in requests)
			{
				if (request.DownloaderType.Contains("ADSL") &&
				    string.IsNullOrWhiteSpace(request.GetHeader(Consts.RedialRegExp)))
				{
					throw new ArgumentException(
						$"Request {request.RequestUri} set to use ADSL but RedialEgeExp is empty");
				}

				request.RequestedTimes += 1;

				// 1. 请求次数超过限制则跳过，并添加失败记录
				// 2. 默认构造的请求次数为 0， 并且不可用户更改，因此可以保证数据安全性
				if (request.RequestedTimes > _options.RetriedTimes)
				{
					await _services.StatisticsClient.IncreaseFailureAsync(Id);
					continue;
				}

				// 1. 默认构造的深度为 0， 并且用户不可更改，可以保证数据安全
				// 2. 当深度超过限制则跳过
				if (_options.Depth > 0 && request.Depth > _options.Depth)
				{
					continue;
				}

				request.Owner = Id;
				list.Add(request);
			}

			var count = await _services.Scheduler.EnqueueAsync(list);
			return count;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			var tuple = GetIdAndName();
			tuple.Id.NotNullOrWhiteSpace("Id");
			if (tuple.Id.Length > 36)
			{
				throw new ArgumentException("Id 长度不能超过 36 个字符");
			}

			Id = tuple.Id;
			Name = tuple.Name;
			Logger.LogInformation($"Initialize {Id}, {Name}");
			await _services.StatisticsClient.StartAsync(Id, Name);
			await InitializeAsync(stoppingToken);
			await LoadRequestsFromSuppliers(stoppingToken);
			await InitializeDataFlowAsync();
			await _services.StatisticsClient.IncreaseTotalAsync(Id, _services.Scheduler.Total);
			await RegisterConsumerAsync(stoppingToken);
			await RunAsync(stoppingToken);
			Logger.LogInformation($"{Id}, {Name} started");
		}

		private async Task RegisterConsumerAsync(CancellationToken stoppingToken)
		{
			var topic = string.Format(TopicNames.Spider, Id.ToUpper());
			_consumer = new AsyncMessageConsumer<byte[]>(topic);
			_consumer.Received += async (bytes) =>
			{
				var message = await bytes.DeserializeAsync(stoppingToken);
				if (message == null)
				{
					Logger.LogWarning("Received empty message");
					return;
				}

				if (message is Exit exit)
				{
					if (exit.Id == Id)
					{
						_services.ApplicationLifetime.StopApplication();
					}
				}
				else if (message is Response response)
				{
					// 1. 从请求队列中去除请求
					var request = _requestedQueue.Dequeue(response.RequestHash);
					if (response.StatusCode == HttpStatusCode.OK)
					{
						request.Agent = response.Agent;
						await _services.StatisticsClient.IncreaseAgentSuccessAsync(response.Agent,
							response.ElapsedMilliseconds);
						await HandleResponseAsync(request, response, bytes);
					}
					else
					{
						await _services.StatisticsClient.IncreaseAgentFailureAsync(response.Agent,
							response.ElapsedMilliseconds);
						var exception = Encoding.UTF8.GetString(response.Content.Data);
						if (_services.IsDistributed)
						{
							Logger.LogError($"Agent request {request.RequestUri} failed: {exception}");
						}

						// 每次调用添加会导致 Requested + 1, 因此失败多次的请求最终会被过滤不再加到调度队列
						await AddRequestsAsync(request);
					}
				}
			};
			await _services.MessageQueue.ConsumeAsync(_consumer, stoppingToken);
		}

		private async Task HandleResponseAsync(Request request, Response response, byte[] responseBytes)
		{
			try
			{
				using var scope = _services.ServiceProvider.CreateScope();
				var context = new DataContext(scope.ServiceProvider, _options, request, response);
				context.AddData(Consts.ResponseBytes, responseBytes);

				foreach (var dataFlow in _dataFlows)
				{
					await dataFlow.HandleAsync(context);
				}

				var count = await AddRequestsAsync(context.FollowRequests);
				await _services.StatisticsClient.IncreaseTotalAsync(Id, count);
				await _services.StatisticsClient.IncreaseSuccessAsync(Id);
			}
			catch (Exception e)
			{
				// if download correct content, parser or storage failed by network or something else
				// retry it until trigger retryTimes limitation
				await AddRequestsAsync(request);
				Logger.LogError($"Handle {JsonConvert.SerializeObject(request)} failed: {e}");
			}
		}

		private async Task RunAsync(CancellationToken stoppingToken)
		{
			var tuple = ComputeIntervalAndDequeueBatch(_options.Speed);
			var sleepTimeLimit = _options.EmptySleepTime * 1000;

			await Task.Factory.StartNew(async () =>
			{
				try
				{
					var pausedTime = 0;
					var sleepTime = 0;
					var printFlag = 0;
					while (!stoppingToken.IsCancellationRequested)
					{
						printFlag += tuple.Interval;
						if (printFlag >= 5000)
						{
							printFlag = 0;
							await _services.StatisticsClient.PrintAsync(Id);
						}

						if (_requestedQueue.Count > _options.RequestedQueueCount)
						{
							if (pausedTime > sleepTimeLimit)
							{
								Logger.LogInformation(
									$"{Id}, {Name} paused too much time");
								break;
							}

							pausedTime += tuple.Interval;
							Logger.LogInformation(
								$"{Id}, {Name} too much requests enqueued");
							continue;
						}

						pausedTime = 0;
						var timeoutRequests = _requestedQueue.GetAllTimeoutList();
						if (timeoutRequests.Length > 0)
						{
							foreach (var request in timeoutRequests)
							{
								Logger.LogWarning(
									$"{Id}, {Name} request {request.RequestUri} timeout");
							}

							await AddRequestsAsync(timeoutRequests);
						}
						else
						{
							var requests = (await _services.Scheduler.DequeueAsync(tuple.Batch)).ToArray();

							if (requests.Length > 0)
							{
								sleepTime = 0;
							}
							else
							{
								sleepTime += tuple.Interval;
								if (sleepTime > sleepTimeLimit)
								{
									break;
								}
							}

							foreach (var request in requests)
							{
								ConfigureRequest(request);
							}

							if (!await PublishRequestMessagesAsync(requests))
							{
								break;
							}
						}

						await Task.Delay(tuple.Interval, default);
					}
				}
				catch (Exception e)
				{
					Logger.LogError($"{Id}, {Name} exited by exception: {e}");
				}
				finally
				{
					await ExitAsync();
				}
			}, stoppingToken);
		}

		private async Task ExitAsync()
		{
			await _services.StatisticsClient.ExitAsync(Id);
			_services.ApplicationLifetime.StopApplication();
		}

		private (int Interval, int Batch) ComputeIntervalAndDequeueBatch(double speed)
		{
			if (speed >= 1)
			{
				var interval = 1000;
				var batch = (int)speed;
				return (interval, batch);
			}
			else
			{
				var interval = (int)(1 / speed) * 1000;
				var batch = 1;
				return (interval, batch);
			}
		}

		private async Task<bool> PublishRequestMessagesAsync(params Request[] requests)
		{
			if (requests.Length > 0)
			{
				foreach (var request in requests)
				{
					if (_options.UseProxy)
					{
						var proxy = await _services.ProxyPool.GetAsync(70);
						if (proxy == null)
						{
							Logger.LogError("Exit because there is no available proxy");
							return false;
						}

						request.Proxy = proxy.Uri;
					}

					string topic;
					request.Timestamp = DateTimeOffset.Now.ToTimestamp();
					if (string.IsNullOrWhiteSpace(request.Agent))
					{
						topic = string.IsNullOrEmpty(request.DownloaderType)
							? DownloaderTypeNames.HttpClient
							: request.DownloaderType;
					}
					else
					{
						switch (request.Policy)
						{
							// 非初始请求如果是链式模式则使用旧的下载器
							case RequestPolicy.Chained:
							{
								topic = $"{request.Agent}".ToUpper();
								break;
							}
							case RequestPolicy.Random:
							{
								topic = string.IsNullOrEmpty(request.DownloaderType)
									? DownloaderTypeNames.HttpClient
									: request.DownloaderType;
								break;
							}
							default:
							{
								throw new ApplicationException($"Not supported policy: {request.Policy}");
							}
						}
					}

					if (_requestedQueue.Enqueue(request))
					{
						await _services.MessageQueue.PublishAsBytesAsync(topic, request);
						if (_services.IsDistributed)
						{
							Logger.LogInformation($"{request.RequestUri} publish request success");
						}
					}
				}
			}

			return true;
		}

		private async Task LoadRequestsFromSuppliers(CancellationToken stoppingToken)
		{
			// 通过供应接口添加请求
			foreach (var requestSupplier in _requestSuppliers)
			{
				foreach (var request in await requestSupplier.GetAllListAsync(stoppingToken))
				{
					await AddRequestsAsync(request);
				}

				Logger.LogInformation(
					$"{Id}, {Name} load requests from {requestSupplier.GetType().Name} {_requestSuppliers.IndexOf(requestSupplier)}/{_requestSuppliers.Count}");
			}
		}

		private async Task InitializeDataFlowAsync()
		{
			if (_dataFlows.Count == 0)
			{
				Logger.LogWarning("There is no any dataFlow");
			}
			else
			{
				var dataFlowInfo = string.Join(" -> ", _dataFlows.Select(x => x.GetType().Name));
				Logger.LogInformation($"{Id}, {Name} DataFlows: {dataFlowInfo}");
				foreach (var dataFlow in _dataFlows)
				{
					dataFlow.SetLogger(Logger);
					try
					{
						await dataFlow.InitAsync();
					}
					catch (Exception e)
					{
						Logger.LogError($"Init dataFlow {dataFlow.GetType().Name} failed: {e}");
						_services.ApplicationLifetime.StopApplication();
					}
				}
			}
		}
	}
}
