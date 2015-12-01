﻿// Seq Client for .NET - Copyright 2014 Continuous IT Pty Ltd
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.PeriodicBatching;

namespace Serilog.Sinks.Seq
{
    class SeqSink : PeriodicBatchingSink
    {
        readonly string _apiKey;
        readonly long? _eventPayloadLimitBytes;
        readonly HttpClient _httpClient;
        const string BulkUploadResource = "api/events/raw";
        const string ApiKeyHeaderName = "X-Seq-ApiKey";
        
        LogEventLevel? _minimumAcceptedLevel;

        static readonly TimeSpan RequiredLevelCheckInterval = TimeSpan.FromMinutes(2);
        DateTime _nextRequiredLevelCheckUtc = DateTime.UtcNow.Add(RequiredLevelCheckInterval);

        public const int DefaultBatchPostingLimit = 1000;
        public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(2);

        public SeqSink(string serverUrl, string apiKey, int batchPostingLimit, TimeSpan period, long? eventPayloadLimitBytes)
            : base(batchPostingLimit, period)
        {
            if (serverUrl == null) throw new ArgumentNullException(nameof(serverUrl));
            _apiKey = apiKey;
            _eventPayloadLimitBytes = eventPayloadLimitBytes;

            var baseUri = serverUrl;
            if (!baseUri.EndsWith("/"))
                baseUri += "/";

            _httpClient = new HttpClient { BaseAddress = new Uri(baseUri) };
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
                _httpClient.Dispose();
        }

        // The sink must emit at least one event on startup, and the server be
        // configured to set a specific level, before background level checks will be performed.
        protected override void OnEmptyBatch()
        {
            if (_minimumAcceptedLevel != null &&
                _nextRequiredLevelCheckUtc < DateTime.UtcNow)
            {
                EmitBatch(Enumerable.Empty<LogEvent>());
            }
        }

        protected override async Task EmitBatchAsync(IEnumerable<LogEvent> events)
        {
            _nextRequiredLevelCheckUtc = DateTime.UtcNow.Add(RequiredLevelCheckInterval);

            var payload = new StringWriter();
            payload.Write("{\"Events\":[");

            var formatter = new JsonFormatter(closingDelimiter: "");
            var delimStart = "";
            foreach (var logEvent in events)
            {
                if (_eventPayloadLimitBytes.HasValue)
                {
                    var scratch = new StringWriter();
                    formatter.Format(logEvent, scratch);
                    var buffered = scratch.ToString();

                    if (Encoding.UTF8.GetByteCount(buffered) > _eventPayloadLimitBytes.Value)
                    {
                        SelfLog.WriteLine("Event JSON representation exceeds the byte size limit of {0} set for this sink and will be dropped; data: {1}", _eventPayloadLimitBytes, buffered);
                    }
                    else
                    {
                        payload.Write(delimStart);
                        payload.Write(buffered);
                        delimStart = ",";
                    }
                }
                else
                {
                    payload.Write(delimStart);
                    formatter.Format(logEvent, payload);
                    delimStart = ",";
                }
            }

            payload.Write("]}");

            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(_apiKey))
                content.Headers.Add(ApiKeyHeaderName, _apiKey);
    
            var result = await _httpClient.PostAsync(BulkUploadResource, content);
            if (!result.IsSuccessStatusCode)
                throw new LoggingFailedException($"Received failed result {result.StatusCode} when posting events to Seq");

            var returned = await result.Content.ReadAsStringAsync();
            _minimumAcceptedLevel = SeqApi.ReadEventInputResult(returned);
        }

        protected override bool CanInclude(LogEvent evt)
        {
            return _minimumAcceptedLevel == null ||
                (int)_minimumAcceptedLevel <= (int)evt.Level;
        }
    }
}
