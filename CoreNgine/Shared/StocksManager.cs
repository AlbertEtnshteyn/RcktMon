﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreData;
using CoreData.Interfaces;
using CoreData.Models;
using CoreNgine.Data;
using CoreNgine.Infra;
using CoreNgine.Models;
using Microsoft.Extensions.Logging;
using Tinkoff.Trading.OpenApi.Models;
using Tinkoff.Trading.OpenApi.Network;
using static Tinkoff.Trading.OpenApi.Models.StreamingRequest;

namespace CoreNgine.Shared
{
    public class StocksManager
    {
        private readonly IMainModel _mainModel;
        private DateTime? _lastEventReceived = null;

        private readonly ILogger<StocksManager> _logger;
        
        private readonly HashSet<string> _subscribedFigi = new HashSet<string>();
        private readonly HashSet<string> _subscribedMinuteFigi = new HashSet<string>();
        private TelegramManager _telegram;

        private readonly ConcurrentQueue<BrokerAction> CommonConnectionActions = new ConcurrentQueue<BrokerAction>();
        private ConcurrentQueue<object> _stockProcessingQueue = new ConcurrentQueue<object>();
        private ConcurrentQueue<IStockModel> _monthStatsQueue = new ConcurrentQueue<IStockModel>();
        private Task _monthStatsTask;
        private Task CommonConnectionQueueTask;
        private Task[] _responseProcessingTasks;


        private Connection CommonConnection { get; set; }
        private Connection CandleConnection { get; set; }
        private Connection InstrumentInfoConnection { get; set; }

        internal string TiApiToken => Settings.TiApiKey;
        internal string TgBotToken => Settings.TgBotApiKey;
        internal long TgChatId 
        {
            get
            {
                return long.TryParse(Settings.TgChatId, out long result ) ? result : long.MinValue;
            }
        }
        
        public TelegramManager Telegram => _telegram;

        private readonly IServiceProvider _services;
        private readonly ISettingsProvider _settingsProvider;

        public IEventAggregator2 EventAggregator { get; }

        public INgineSettings Settings => _settingsProvider.Settings;

        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        
        public StocksManager(IServiceProvider services, IMainModel mainModel, ILogger<StocksManager> logger, ISettingsProvider settingsProvider, IEventAggregator2 eventAggregator)
        {
            _settingsProvider = settingsProvider;
            _services = services;
            _mainModel = mainModel;
            _logger = logger;
            EventAggregator = eventAggregator;

            Init();
        }

        private void PrepareConnection()
        {
            if (CommonConnection != null)
            {
                _stockProcessingQueue.Clear();
                CommonConnection.StreamingEventReceived -= Broker_StreamingEventReceived;
                CommonConnection.Dispose();
            }
            if (InstrumentInfoConnection != null)
            {
                InstrumentInfoConnection.StreamingEventReceived -= Broker_StreamingEventReceived;
                InstrumentInfoConnection.Dispose();
            }

            if (CandleConnection != null)
            {
                CandleConnection.StreamingEventReceived -= Broker_StreamingEventReceived;
                CandleConnection.Dispose();
            }

            CommonConnection = ConnectionFactory.GetConnection(TiApiToken);
            CandleConnection = ConnectionFactory.GetConnection(TiApiToken);
            InstrumentInfoConnection = ConnectionFactory.GetConnection(TiApiToken);
            CandleConnection.StreamingEventReceived += Broker_StreamingEventReceived;
            InstrumentInfoConnection.StreamingEventReceived += Broker_StreamingEventReceived;
            CommonConnection.StreamingEventReceived += Broker_StreamingEventReceived;

            RunMonthUpdateTaskIfNotRunning();
        }

        public void Init()
        {
            if (_telegram != null)
                _telegram.Stop();

            if (TgBotToken != null && TgChatId > long.MinValue) 
                _telegram = new TelegramManager(_services, TgBotToken, TgChatId);

            if (TiApiToken == null)
               return; 

            PrepareConnection();

            if (CommonConnectionQueueTask == null)
            {
                CommonConnectionQueueTask = Task.Factory
                    .StartNew(() => BrokerQueueLoopAsync()
                            .ConfigureAwait(false),
                    _cancellationTokenSource.Token,
                    TaskCreationOptions.LongRunning, 
                    TaskScheduler.Default);

                _responseProcessingTasks = new Task[1];
                for (int i = 0; i < _responseProcessingTasks.Length; i++)
                {
                    _responseProcessingTasks[i] = Task.Factory
                        .StartNew(() => RespProcessingLoopAsync()
                                .ConfigureAwait(false),
                            _cancellationTokenSource.Token,
                            TaskCreationOptions.LongRunning, 
                            TaskScheduler.Default);
                }
            }
        }

        private async Task RespProcessingLoopAsync()
        {
            var cancellationToken = _cancellationTokenSource.Token;
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_stockProcessingQueue.TryDequeue(out var obj))
                {
                    if (obj is CandleResponse cr)
                    {
                        try
                        {
                            await CandleProcessingProc(cr);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("Error while processing candle {candle}: {error}", cr, ex.Message);
                        }
                    }
                    else if (obj is IStockModel stock)
                    {
                        await _mainModel.OnStockUpdated(stock);
                    }
                }

                await Task.Delay(100);
            }
        }

        public void QueueBrokerAction(Func<Connection, Task> act, string description)
        {
            var brAct = new BrokerAction(act, description);
            CommonConnectionActions.Enqueue(brAct);
        }

        private async Task BrokerQueueLoopAsync()
        {
            var cancellationToken = _cancellationTokenSource.Token;

            while (!cancellationToken.IsCancellationRequested)
            {
                while (CommonConnectionActions.TryDequeue(out BrokerAction act))
                {
                    try
                    {
                        //var msg = $"{DateTime.Now} Выполнение операции '{act.Description}'...";
                        _logger.LogTrace("Выполнение операции {OperationDescription}", act.Description);
                        //Debug.WriteLine(msg);
                        await act.Action(CommonConnection);
                    }
                    catch (Exception ex)
                    {
                        //CommonConnectionActions.Push(act);
                        var errorMsg = $"Ошибка при выполнении операции '{act.Description}': {ex.Message}";
                        LogError(errorMsg);
                        await ResetConnection(errorMsg);
                    }
                }

                if (_lastEventReceived != null && DateTime.Now.Subtract(_lastEventReceived.Value).TotalSeconds > 5)
                {
                    if (!_mainModel.Stocks.Values.Any(s => s.Status != null && s.Status != "not_available_for_trading") 
                        || (DateTime.Now.Hour < 10 && (DateTime.Now.Hour > 1 || DateTime.Now.Hour == 1 && DateTime.Now.Minute >= 45)))
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    await ResetConnection("Данные не поступали дольше 5 секунд");
                }

                await Task.Delay(100);
            }
        }

        private void LogError(string msg)
        {
            _mainModel.AddMessage(
                ticker: "ERROR",
                date: DateTime.Now,
                text: msg
            );
            _logger.LogError(msg);
            //Debug.WriteLine(msg);
        }

        public async Task ResetConnection(string errorMsg)
        {
            LogError("Переподключение: " + errorMsg);
            await ResetConnection();
        }

        public async Task ResetConnection()
        {
            _lastEventReceived = null;
            _stockProcessingQueue.Clear();
            CommonConnectionActions.Clear();
            _subscribedFigi.Clear();
            _subscribedMinuteFigi.Clear();
            PrepareConnection();
            await UpdatePrices();
        }

        private async Task CandleProcessingProc(CandleResponse cr)
        {
            _lastEventReceived = DateTime.Now;
                var candle = cr.Payload;
                var stock = _mainModel.Stocks.Values.FirstOrDefault(s => s.Figi == candle.Figi);
                if (stock != null)
                {
                    //stock.IsNotifying = false;
                    if (candle.Interval == CandleInterval.Day)
                    {
                        if (candle.Time.Date < DateTime.Now.Date.Subtract(TimeSpan.FromHours(3)) && stock.LastUpdate > DateTime.MinValue)
                            return;
                        stock.TodayOpen = candle.Open;
                        stock.TodayDate = candle.Time.ToLocalTime();
                        stock.LastUpdate = DateTime.Now;
                        stock.Price = candle.Close;
                        if (stock.TodayOpen > 0)
                            stock.DayChange = (stock.Price - stock.TodayOpen) / stock.TodayOpen;
                        stock.DayVolume = Math.Truncate(candle.Volume);
                        if (stock.AvgDayVolumePerMonth > 0)
                            stock.DayVolChgOfAvg = stock.DayVolume / stock.AvgDayVolumePerMonth;
                        await _mainModel.OnStockUpdated(stock);
                        if (!_subscribedMinuteFigi.Contains(stock.Figi))
                        {
                            QueueBrokerAction(b => b.SendStreamingRequestAsync(
                                    SubscribeCandle(stock.Figi, CandleInterval.Minute)),
                                $"Подписка на минутную свечу {stock.Ticker} ({stock.Figi})");
                        }
                    }
                    else if (candle.Interval == CandleInterval.Minute)
                    {
                        if (candle.Time.Date > stock.TodayDate && candle.Time.ToLocalTime().Hour > 3 && stock.MinuteCandles.Count > 1)
                        {
                            await ResetConnection(
                                $"Новый день ({stock.Ticker} {stock.TodayDate} -> {candle.Time.Date})");
                            return;
                        }
                        stock.LastUpdate = DateTime.Now;
                        stock.LogCandle(candle);
                        await _mainModel.OnStockUpdated(stock);
                    }
                }
        }

        private async void Broker_StreamingEventReceived(object sender, StreamingEventReceivedEventArgs e)
        {
            //Debug.WriteLine(JsonConvert.SerializeObject(e.Response));
            _lastEventReceived = DateTime.Now;
            switch (e.Response)
            {
                case CandleResponse cr:
                    {
                        _stockProcessingQueue.Enqueue(cr);
                        break;
                    }

                case OrderbookResponse or:
                    {
                        var stock = _mainModel.Stocks.Values.FirstOrDefault(s => s.Figi == or.Payload.Figi);
                        if (stock != null && or.Payload.Asks.Count > 0 && or.Payload.Bids.Count > 0)
                        {
                            stock.BestBidPrice = or.Payload.Bids[0][0];
                            stock.BestAskPrice = or.Payload.Asks[0][0];
                            _stockProcessingQueue.Enqueue(stock); // raise stock update (but only in sync with other updates)
                        }

                        break;
                    }

                case InstrumentInfoResponse ir:
                    {
                        var info = ir.Payload;
                        var stock = _mainModel.Stocks.Values.FirstOrDefault(s => s.Figi == info.Figi);
                        if (stock != null)
                        {
                            stock.Status = info.TradeStatus;
                        }
                        break;
                    }
            }
        }

        private int _apiCount = 0;

        private async Task<bool> GetMonthStats(IStockModel stock)
        {
            if (!stock.MonthStatsExpired)
                return true;

            _apiCount++;
            //Debug.WriteLine($"API Request {++_apiCount} for {stock.Ticker} {stock.PriceF} {stock.DayChangeF} {DateTime.Now}");

            CandleList prices = null;
            try
            {
                prices = await CommonConnection.Context.MarketCandlesAsync(stock.Figi,
                    DateTime.Now.Date.AddMonths(-1),
                    DateTime.Now.Date.AddDays(1), CandleInterval.Day);
                stock.LastMonthDataUpdate = DateTime.Now;
            }
            catch
            {
                return false;
            }

            decimal monthVolume = 0, monthHigh = 0, monthLow = 0, monthAvgPrice = 0, 
                avgDayVolumePerMonth = 0, avgDayPricePerMonthCost = 0, monthOpen = -1,
                yesterdayVolume = 0, yesterdayMin = 0, yesterdayMax = 0, yesterdayAvgPrice = 0;

            var todayCandle = prices.Candles[prices.Candles.Count-1];
            foreach (var candle in prices.Candles)
            {
                if (candle == todayCandle)
                {
                    stock.DayVolume = candle.Volume;
                }
                else
                {
                    if (monthOpen == -1)
                        monthOpen = candle.Open;
                    monthLow = monthLow == 0 ? candle.Low : Math.Min(monthLow, candle.Low);
                    monthHigh = monthHigh == 0 ? candle.High : Math.Min(monthHigh, candle.High);
                    monthVolume += candle.Volume;
                    avgDayPricePerMonthCost += (candle.High + candle.Low) / 2;
                    yesterdayVolume = candle.Volume;
                    yesterdayMin = candle.Low;
                    yesterdayMax = candle.High;
                }
                AddCandleToStock(Tuple.Create(stock, candle));
            }

            monthAvgPrice = (monthLow + monthHigh) / 2;
            yesterdayAvgPrice = (yesterdayMin + yesterdayMax) / 2;
            avgDayVolumePerMonth = monthVolume / prices.Candles.Count;
            avgDayPricePerMonthCost /= prices.Candles.Count;
            
            stock.MonthOpen = monthOpen;
            stock.MonthHigh = monthHigh;
            stock.MonthLow = monthLow;
            stock.MonthVolume = monthVolume;
            stock.MonthVolumeCost = monthVolume * monthAvgPrice * stock.Lot;
            stock.AvgDayVolumePerMonth = Math.Round(avgDayVolumePerMonth);
            stock.AvgDayPricePerMonth = avgDayPricePerMonthCost;
            stock.AvgDayVolumePerMonthCost = avgDayPricePerMonthCost * avgDayVolumePerMonth * stock.Lot;
            stock.DayVolChgOfAvg = stock.DayVolume / stock.AvgDayVolumePerMonth;
            stock.YesterdayVolume = yesterdayVolume;
            stock.YesterdayVolumeCost = yesterdayVolume * yesterdayAvgPrice;
            stock.YesterdayAvgPrice = yesterdayAvgPrice;

            return true;
        }

        public async Task<bool> CheckMonthStatsAsync(IStockModel stock, CancellationToken token = default(CancellationToken))
        {
            if (stock.MonthStatsExpired)
            {
                lock (stock)
                {
                    if (!_monthStatsQueue.Contains(stock))
                    {
                        _monthStatsQueue.Enqueue(stock);
                    }
                }

                while (stock.MonthStatsExpired && !token.IsCancellationRequested)
                {
                    await Task.Delay(100, token);
                }

                if (token.IsCancellationRequested)
                    return false;
            }

            return true;
        }

        private void EnqueueStockForMonthStatsIfExpired(IStockModel stock)
        {
            if (stock.MonthStatsExpired)
            {
                lock (stock)
                {
                    if (stock.MonthStatsExpired && !_monthStatsQueue.Contains(stock))
                    {
                        _monthStatsQueue.Enqueue(stock);
                    }
                }
            }
        }

        private DateTime _lastBatchMonthStatsEnqueued;

        private async Task ReportStatsCheckerProgress()
        {
            var stocks = _mainModel.Stocks.Values.Where(s => s.Price > 0).ToList();
            var completed = stocks.Count(s => !s.MonthStatsExpired);
            await EventAggregator.PublishOnCurrentThreadAsync(new StatsUpdateMessage(completed, stocks.Count,
                completed == stocks.Count, _apiCount));
        }

        private async Task MonthStatsCheckerLoop()
        {
            var token = _cancellationTokenSource.Token;
            _lastBatchMonthStatsEnqueued = DateTime.Now;

            while (!token.IsCancellationRequested)
            {
                if (_monthStatsQueue.TryDequeue(out var stock))
                {
                    try
                    {
                        if (await GetMonthStats(stock))
                            await ReportStatsCheckerProgress();
                        else
                        {
                            await Task.Delay(500);
                            EnqueueStockForMonthStatsIfExpired(stock);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка при загрузке истории");
                        EnqueueStockForMonthStatsIfExpired(stock);
                    }
                }

                if (DateTime.Now.Subtract(_lastBatchMonthStatsEnqueued).TotalSeconds > 31)
                {
                    _mainModel.Stocks
                        .Select(s => s.Value)
                        .Where(s => !_monthStatsQueue.Contains(s)
                                    && s.MonthStatsExpired && s.Price > 0)
                        .OrderByDescending(s => Math.Abs(s.DayChange))
                        .Take(30).ToList().ForEach(s => _monthStatsQueue.Enqueue(s));
                    _lastBatchMonthStatsEnqueued = DateTime.Now;
                }

                if (!_monthStatsQueue.Any())
                    await Task.Delay(100);
            }
        }

        private void RunMonthUpdateTaskIfNotRunning()
        {
            if (_monthStatsTask == null)
                _monthStatsTask = Task.Factory.StartLongRunningTask(
                    () => MonthStatsCheckerLoop().ConfigureAwait(false), 
                    _cancellationTokenSource.Token);
        }

        public async Task UpdatePrices()
        {
            var toSubscribeInstr = new HashSet<IStockModel>();
            foreach (var stock in _mainModel.Stocks.Values)
            {
                if (!_subscribedFigi.Contains(stock.Figi))
                {
                    var request = new CandleSubscribeRequest(stock.Figi, CandleInterval.Day);
                    QueueBrokerAction(b => b.SendStreamingRequestAsync(request),
                        $"Подписка на дневную свечу {stock.Ticker} ({stock.Figi})");

                    var request2 = new OrderbookSubscribeRequest(stock.Figi, 2);
                    QueueBrokerAction(b => CandleConnection.SendStreamingRequestAsync(request2),
                        $"Подписка на стакан {stock.Ticker} ({stock.Figi}");

                    toSubscribeInstr.Add(stock);
                    _subscribedFigi.Add(stock.Figi);
                }
            }

            int n = 0;
            foreach (var stock in toSubscribeInstr)
            {
                var request3 = new InstrumentInfoSubscribeRequest( stock.Figi );
                QueueBrokerAction( b => InstrumentInfoConnection.SendStreamingRequestAsync( request3 ),
                    $"Подписка на статус {stock.Ticker} ({stock.Figi}" );

                if (++n % 100 == 0)
                    await Task.Delay(1000);
            }
        }

        private void AddCandleToStock(object data)
        {
            if (data is Tuple<IStockModel, CandlePayload> stocandle)
            {
                var stock = stocandle.Item1;
                var candle = stocandle.Item2;
                if (stock.Candles.Any(c => c.Time == candle.Time && c.Interval == candle.Interval))
                    return;
                stock.AddCandle(candle);
                //stock.LastUpdate = DateTime.Now;
            }
        }

        public async Task UpdateStocks()
        {
            if (CommonConnection == null)
                return;
            var stocks = await CommonConnection.Context.MarketStocksAsync();
            var stocksToAdd = new HashSet<IStockModel>();
            foreach (var instr in stocks.Instruments)
            {
                var stock = _mainModel.Stocks.Values.FirstOrDefault(s => s.Figi == instr.Figi);
                if (stock == null)
                {
                    stock = _mainModel.CreateStockModel(instr);
                    stocksToAdd.Add(stock);
                }
            }
            await _mainModel.AddStocks(stocksToAdd);
            await UpdatePrices();
            //_mainModel.IsNotifying = false;
        }
    }
}