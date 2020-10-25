﻿using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Prometheus;
using SloCovidServer.Mappers;
using SloCovidServer.Models;
using SloCovidServer.Services.Abstract;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SloCovidServer.Services.Implemented
{
    public class Communicator : ICommunicator
    {
        // const string root = "https://raw.githubusercontent.com/sledilnik/data/master/csv";
        readonly string root = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_DATA_SOURCE_ROOT")) ? "https://raw.githubusercontent.com/sledilnik/data/master/csv" : Environment.GetEnvironmentVariable("API_DATA_SOURCE_ROOT");
        readonly HttpClient client;
        readonly ILogger<Communicator> logger;
        readonly Mapper mapper;
        readonly ISlackService slackService;
        protected static readonly Histogram RequestDuration = Metrics.CreateHistogram("source_request_duration_milliseconds",
                "Request duration to CSV sources in milliseconds",
                new HistogramConfiguration
                {
                    Buckets = Histogram.ExponentialBuckets(start: 20, factor: 2, count: 10),
                    LabelNames = new[] { "endpoint", "is_exception" }
                });
        protected static readonly Counter RequestCount = Metrics.CreateCounter("source_request_total", "Total number of requests to source",
                new CounterConfiguration
                {
                    LabelNames = new[] { "endpoint" }
                });
        protected static readonly Counter RequestMissedCache = Metrics.CreateCounter("source_request_missed_cache_total",
                "Total number of missed cache when fetching from source",
                new CounterConfiguration
                {
                    LabelNames = new[] { "endpoint" }
                });
        protected static readonly Counter RequestExceptions = Metrics.CreateCounter("source_request_exceptions_total",
                "Total number of exceptions when fetching data from source",
                new CounterConfiguration
                {
                    LabelNames = new[] { "endpoint" }
                });
        protected static readonly Gauge EndpointDown = Metrics.CreateGauge("endpoint_down",
       "When above 0 means that given endpoint is unreachable",
       new GaugeConfiguration
       {
           LabelNames = new[] { "endpoint" }
       });
        readonly ArrayEndpointCache<StatsDaily> statsCache;
        readonly ArrayEndpointCache<RegionsDay> regionCache;
        readonly ArrayEndpointCache<PatientsDay> patientsCache;
        readonly ArrayEndpointCache<HospitalsDay> hospitalsCache;
        readonly ArrayEndpointCache<Hospital> hospitalsListCache;
        readonly ArrayEndpointCache<Municipality> municipalitiesListCache;
        readonly ArrayEndpointCache<RetirementHome> retirementHomesListCache;
        readonly ArrayEndpointCache<RetirementHomesDay> retirementHomesCache;
        readonly ArrayEndpointCache<DeceasedPerRegionsDay> deceasedPerRegionsDayCache;
        readonly ArrayEndpointCache<MunicipalityDay> municipalityDayCache;
        readonly ArrayEndpointCache<HealthCentersDay> healthCentersDayCache;
        readonly ArrayEndpointCache<StatsWeeklyDay> statsWeeklyDayCache;
        /// <summary>
        /// Holds error flags against endpoints
        /// </summary>
        readonly ConcurrentDictionary<string, object> errors;
        public Communicator(ILogger<Communicator> logger, Mapper mapper, ISlackService slackService)
        {
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            this.logger = logger;
            this.mapper = mapper;
            this.slackService = slackService;
            statsCache = new ArrayEndpointCache<StatsDaily>();
            regionCache = new ArrayEndpointCache<RegionsDay>();
            patientsCache = new ArrayEndpointCache<PatientsDay>();
            hospitalsCache = new ArrayEndpointCache<HospitalsDay>();
            hospitalsListCache = new ArrayEndpointCache<Hospital>();
            municipalitiesListCache = new ArrayEndpointCache<Municipality>();
            retirementHomesListCache = new ArrayEndpointCache<RetirementHome>();
            retirementHomesCache = new ArrayEndpointCache<RetirementHomesDay>();
            deceasedPerRegionsDayCache = new ArrayEndpointCache<DeceasedPerRegionsDay>();
            municipalityDayCache = new ArrayEndpointCache<MunicipalityDay>();
            healthCentersDayCache = new ArrayEndpointCache<HealthCentersDay>();
            statsWeeklyDayCache = new ArrayEndpointCache<StatsWeeklyDay>();
            errors = new ConcurrentDictionary<string, object>();
            Task.Run(this.CacheRefresher);
        }

        public async Task CacheRefresher()
        {
            logger.LogInformation($"Initializing cache refresher");
            while (true)
            {
                logger.LogInformation($"Refreshing GH cache");
                var delay = Task.Delay(TimeSpan.FromSeconds(60));

                var stats = this.RefreshEndpointCache($"{root}/stats.csv", this.statsCache, mapper.GetStatsFromRaw);
                var regions = this.RefreshEndpointCache($"{root}/regions.csv", this.regionCache, mapper.GetRegionsFromRaw);
                var patients = this.RefreshEndpointCache($"{root}/patients.csv", this.patientsCache, mapper.GetPatientsFromRaw);
                var hospitals = this.RefreshEndpointCache($"{root}/hospitals.csv", this.hospitalsCache, mapper.GetHospitalsFromRaw);
                var hospitalsList = this.RefreshEndpointCache($"{root}/dict-hospitals.csv", this.hospitalsListCache, mapper.GetHospitalsListFromRaw);
                var municipalitiesList = this.RefreshEndpointCache($"{root}/dict-municipality.csv", this.municipalitiesListCache, mapper.GetMunicipalitiesListFromRaw);
                var retirementHomesList = this.RefreshEndpointCache($"{root}/dict-retirement_homes.csv", this.retirementHomesListCache, mapper.GetRetirementHomesListFromRaw);
                var retirementHomes = this.RefreshEndpointCache($"{root}/retirement_homes.csv", this.retirementHomesCache, mapper.GetRetirementHomesFromRaw);
                var deceasedPerRegionsDay = this.RefreshEndpointCache($"{root}/deceased-regions.csv", this.deceasedPerRegionsDayCache, new DeceasedPerRegionsMapper().GetDeceasedPerRegionsDayFromRaw);
                var municipalityDay = this.RefreshEndpointCache($"{root}/municipality.csv", this.municipalityDayCache, new MunicipalitiesMapper().GetMunicipalityDayFromRaw);
                var healthCentersDay = this.RefreshEndpointCache($"{root}/health_centers.csv", this.healthCentersDayCache, new HealthCentersMapper().GetHealthCentersDayFromRaw);
                var statsWeeklyDay = this.RefreshEndpointCache($"{root}/stats-weekly.csv.csv", this.statsWeeklyDayCache, new StatsWeeklyMapper().GetStatsWeeklyDayFromRaw);

                Task.WaitAll(stats, regions, patients, hospitals, hospitalsList, municipalitiesList, retirementHomesList,
                    retirementHomes, deceasedPerRegionsDay, municipalityDay, healthCentersDay, statsWeeklyDay);
                logger.LogInformation($"GH cache refreshed");
                await delay;
            }
        }
        public Task<(ImmutableArray<StatsDaily>? Data, string raw, string ETag, long? Timestamp)> GetStatsAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/stats.csv", statsCache, filter, ct);

        }

        public Task<(ImmutableArray<RegionsDay>? Data, string raw, string ETag, long? Timestamp)> GetRegionsAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/regions.csv", regionCache, filter, ct);
        }

        public Task<(ImmutableArray<PatientsDay>? Data, string raw, string ETag, long? Timestamp)> GetPatientsAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/patients.csv", patientsCache, filter, ct);
        }

        public Task<(ImmutableArray<HospitalsDay>? Data, string raw, string ETag, long? Timestamp)> GetHospitalsAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/hospitals.csv", hospitalsCache, filter, ct);
        }

        public Task<(ImmutableArray<Hospital>? Data, string raw, string ETag, long? Timestamp)> GetHospitalsListAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/dict-hospitals.csv", hospitalsListCache, filter, ct);
        }
        public Task<(ImmutableArray<Municipality>? Data, string raw, string ETag, long? Timestamp)> GetMunicipalitiesListAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/dict-municipality.csv", municipalitiesListCache, filter, ct);
        }

        public Task<(ImmutableArray<RetirementHome>? Data, string raw, string ETag, long? Timestamp)> GetRetirementHomesListAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/dict-retirement_homes.csv", retirementHomesListCache, filter, ct);
        }

        public Task<(ImmutableArray<RetirementHomesDay>? Data, string raw, string ETag, long? Timestamp)> GetRetirementHomesAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/retirement_homes.csv", retirementHomesCache, filter, ct);
        }

        public Task<(ImmutableArray<DeceasedPerRegionsDay>? Data, string raw, string ETag, long? Timestamp)> GetDeceasedPerRegionsAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/deceased-regions.csv", deceasedPerRegionsDayCache, filter, ct);
        }

        public Task<(ImmutableArray<MunicipalityDay>? Data, string raw, string ETag, long? Timestamp)> GetMunicipalitiesAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/municipality.csv", municipalityDayCache, filter, ct);
        }

        public Task<(ImmutableArray<HealthCentersDay>? Data, string raw, string ETag, long? Timestamp)> GetHealthCentersAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/health_centers.csv", healthCentersDayCache, filter, ct);
        }

        public Task<(ImmutableArray<StatsWeeklyDay>? Data, string raw, string ETag, long? Timestamp)> GetStatsWeeklyAsync(string callerEtag, DataFilter filter, CancellationToken ct)
        {
            return GetAsync(callerEtag, $"{root}/stats-weekly.csv", statsWeeklyDayCache, filter, ct);
        }

        public class RegionsPivotCacheData
        {
            public ETagCacheItem<ImmutableArray<Municipality>> Municipalities { get; }
            public ETagCacheItem<ImmutableArray<RegionsDay>> Regions { get; }
            public ImmutableArray<ImmutableArray<object>> Data { get; }
            public RegionsPivotCacheData(ETagCacheItem<ImmutableArray<Municipality>> municipalities, ETagCacheItem<ImmutableArray<RegionsDay>> regions,
                    ImmutableArray<ImmutableArray<object>> data)
            {
                Municipalities = municipalities;
                Regions = regions;
                Data = data;
            }
        }
        RegionsPivotCacheData regionsPivotCacheData = new RegionsPivotCacheData(
                new ETagCacheItem<ImmutableArray<Municipality>>(null, "", ImmutableArray<Municipality>.Empty, timestamp: null),
                new ETagCacheItem<ImmutableArray<RegionsDay>>(null, "", ImmutableArray<RegionsDay>.Empty, timestamp: null),
                data: ImmutableArray<ImmutableArray<object>>.Empty
        );

        async Task<long?> GetTimestampAsync(string url)
        {
            string timestampUrl = $"{url}.timestamp";
            try
            {
                long ts = 0;
                var tsStr = await client.GetStringAsync(timestampUrl);
                long.TryParse(tsStr, out ts);
                return ts;
            }
            catch (HttpRequestException ex) {
                // ignore annoying 404 errors
                return null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error {error} retrieving timestamp from {url}", ex.Message, timestampUrl);
                return null;
            }
        }

        async Task RefreshEndpointCache<TData>(string url, ArrayEndpointCache<TData> sync, Func<string, ImmutableArray<TData>> mapFromString)
        {
            Task ProcessErrorAsync(string message)
            {
                if (errors.TryAdd(url, null))
                {
                    EndpointDown.WithLabels(url).Inc();
                    slackService.SendNotificationAsync($"DATA API REST service started failing to retrieve data from {url} because {message}",
                            CancellationToken.None);
                }
                else
                {
                    slackService.SendNotificationAsync($"DATA API REST service failed retrieving data from {url} because {message}",
                            CancellationToken.None);
                }
                return null;
            }
            Task ProcessErrorRemovalAsync()
            {
                // remove error flag
                if (errors.TryRemove(url, out _))
                {
                    EndpointDown.WithLabels(url).Dec();
                    slackService.SendNotificationAsync($"DATA API REST service started retrieving data from {url}", CancellationToken.None);
                }
                return null;
            }

            var policy = HttpPolicyExtensions
                .HandleTransientHttpError()
                .RetryAsync(1);

            HttpResponseMessage response;

            try
            {
                response = await policy.ExecuteAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    RequestCount.WithLabels(url).Inc();
                    return client.SendAsync(request);
                });
                if (response.IsSuccessStatusCode)
                {
                    _ = ProcessErrorRemovalAsync();
                    var timestamp = await GetTimestampAsync(url);

                    System.Collections.Generic.IEnumerable<string> headerETags;
                    response.Headers.TryGetValues("ETag", out headerETags);
                    string newETag = headerETags != null ? headerETags.SingleOrDefault() : null;
                    string responseBody = await response.Content.ReadAsStringAsync();
                    sync.Cache = new ETagCacheItem<ImmutableArray<TData>>(newETag, responseBody, mapFromString(responseBody), timestamp);
                }
                else
                {
                    _ = ProcessErrorAsync(response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                _ = ProcessErrorAsync(ex.Message);
            }
            return;
        }

        // filters data based on date
        public ImmutableArray<TData> FilterData<TData>(ImmutableArray<TData> data, DataFilter filter)
        {
            if (typeof(IModelDate).IsAssignableFrom(typeof(TData)))
            {
                return data.Where(m =>
                {
                    var md = (IModelDate)m;
                    var date = new DateTime(md.Year, md.Month, md.Day);
                    if (filter.From.HasValue && date < filter.From)
                    {
                        return false;
                    }
                    if (filter.To.HasValue && date > filter.To.Value)
                    {
                        return false;
                    }
                    return true;
                }).ToImmutableArray();
            }
            else
            {
                return data;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TData"></typeparam>
        /// <param name="callerEtag"></param>
        /// <param name="url"></param>
        /// <param name="sync"></param>
        /// <param name="mapFromString"></param>
        /// <param name="filter"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        /// <remarks>
        /// In cache is always all data though filtered is returned.
        /// </remarks>
        async Task<(ImmutableArray<TData>? Data, string raw, string ETag, long? Timestamp)> GetAsync<TData>(string callerEtag, string url,
                EndpointCache<ImmutableArray<TData>> sync, DataFilter filter, CancellationToken ct)
                where TData : class
        {
            var stopwatch = Stopwatch.StartNew();

            bool isException = false;

            string etagInfo = $"ETag {(string.IsNullOrEmpty(callerEtag) ? "none" : $"present {callerEtag}")}";

            try
            {

                ETagCacheItem<ImmutableArray<TData>> current = sync.CacheBlocking;

                if (!String.IsNullOrEmpty(callerEtag) && string.Equals(current.ETag, callerEtag, StringComparison.Ordinal))
                {
                    logger.LogInformation($"Cache hit, client cache hit, {etagInfo}");
                    return (null, current.Raw, current.ETag, current.Timestamp);
                }
                else
                {
                    logger.LogInformation($"Cache hit, client cache refreshed, {etagInfo}");
                    var filteredData = FilterData(current.Data, filter);
                    return (filteredData, current.Raw, current.ETag, current.Timestamp);
                }

                // throw new Exception($"Failed fetching data: {response.ReasonPhrase}");
            }
            catch
            {
                isException = true;
                RequestExceptions.WithLabels(url).Inc();
                throw;
            }
            finally
            {
                RequestDuration.WithLabels(url, isException.ToString()).Observe(stopwatch.ElapsedMilliseconds);
            }
        }
    }
}