using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Xml;
using System.Collections;
using System.Net;

namespace SilkService;

class ETWCollector
{
	public static void StartTrace(CollectorParameters collector)
	{
		// Local variables for StartTrace
		String EventParseSessionName;

		// Is elevated? While running as a service this should always be true but
		// this is kept for edge-case user-fail.
		if (TraceEventSession.IsElevated() != true)
		{
			SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector must be run elevated", true);
			return;
		}

		// Print status
		SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "Starting trace collector", false);

		// We tag event sessions with a unique name
		// While running these are observable with => logman -ets
		if (collector.CollectorType == CollectorType.Kernel)
		{
			EventParseSessionName = KernelTraceEventParser.KernelSessionName;
		}
		else
		{
			String RandId = Guid.NewGuid().ToString();
			EventParseSessionName = ("SilkServiceUserCollector_" + RandId);
		}

		// Create trace session
		using var TraceSession = new TraceEventSession(EventParseSessionName);
		// The collector cannot survive process termination (safeguard)
		TraceSession.StopOnDispose = true;

		// Create event source
		using var EventSource = new ETWTraceEventSource(EventParseSessionName, TraceEventSourceType.Session);
		// A DynamicTraceEventParser can understand how to read the embedded manifests that occur in the dataStream
		var EventParser = new DynamicTraceEventParser(EventSource);

		// _syslogClient = new SysLogClient("logger.ews.lan", 514);
		// var sysLogParts = collector.SysLogPath.Split(':');
		// _syslogClient = new SysLogClient(sysLogParts[1], Convert.ToInt32(sysLogParts[2]));

		// Loop events as they arrive
		EventParser.All += HandleEvent(collector, TraceSession, EventSource);

		// Specify the providers details
		if (collector.CollectorType == CollectorType.Kernel)
		{
			TraceSession.EnableKernelProvider((KernelTraceEventParser.Keywords)collector.KernelKeywords);
		}
		else
		{
			// Note that the collector doesn't know if you specified a wrong provider name,
			// the only tell is that you won't get any events ;)
			ulong.TryParse(collector.UserKeywords.ToString(), System.Globalization.NumberStyles.HexNumber, null, out ulong userKeywords);
			TraceSession.EnableProvider(collector.ProviderName, (TraceEventLevel)collector.UserTraceEventLevel, userKeywords);
		}

		// Write status to eventlog if dictated by the output type
		if (collector.OutputType == OutputType.eventlog)
		{
			String ConvertKeywords;
			if (collector.CollectorType == CollectorType.Kernel)
			{
				ConvertKeywords = Enum.GetName(typeof(KernelTraceEventParser.Keywords), collector.KernelKeywords);
			}
			else
			{
				ConvertKeywords = "0x" + String.Format("{0:X}", (ulong)collector.UserKeywords);
			}
			String Message = $"{{\"Collector\":\"Start\",\"Data\":{{\"Type\":\"{collector.CollectorType}\",\"Provider\":\"{collector.ProviderName}\",\"Keywords\":\"{ConvertKeywords}\",\"FilterOption\":\"{collector.FilterOption}\",\"FilterValue\":\"{collector.FilterValue}\"}}}}";
			WriteEventLogEntry(Message, EventLogEntryType.SuccessAudit, EventIds.Start, collector.Path);
		}

		// Populate the trace bookkeeper
		var CollectorInstance = new CollectorInstance
		{
			CollectorGUID = collector.CollectorGUID,
			EventSource = EventSource,
			EventParseSessionName = EventParseSessionName,
		};
		SilkUtility.CollectorTaskList.Add(CollectorInstance);

		// Signal the ManualResetEvent
		SilkUtility.SignalThreadStarted.Set();

		// Continuously process all new events in the data source
		EventSource.Process();
	}

	static Action<TraceEvent> HandleEvent(CollectorParameters collector, TraceEventSession TraceSession, ETWTraceEventSource EventSource)
	{
		return delegate (TraceEvent data)
		{
			bool processEventData;

			// It's a bit ugly but ... ¯\_(ツ)_/¯
			if (collector.FilterOption != FilterOption.None)
			{
				if (collector.FilterOption == FilterOption.Opcode && (byte)data.Opcode != (byte)collector.FilterValue)
				{
					processEventData = false;
				}
				else if (collector.FilterOption == FilterOption.ProcessID && data.ProcessID != (UInt32)collector.FilterValue)
				{
					processEventData = false;
				}
				else if (collector.FilterOption == FilterOption.ProcessName && data.ProcessName != (String)collector.FilterValue)
				{
					processEventData = false;
				}
				else if (collector.FilterOption == FilterOption.EventName && data.EventName != (String)collector.FilterValue)
				{
					processEventData = false;
				}
				else
				{
					processEventData = true;
				}
			}
			else
			{
				processEventData = true;
			}

			// Only process/serialize events if they match our filter
			if (processEventData)
			{
				var eRecord = new EventRecordStruct
				{
					ProviderGuid = data.ProviderGuid,
					ProviderName = data.ProviderName,
					EventName = data.EventName,
					Opcode = data.Opcode,
					OpcodeName = data.OpcodeName,
					TimeStamp = data.TimeStamp,
					ThreadID = data.ThreadID,
					ProcessID = data.ProcessID,
					ProcessName = data.ProcessName,
					PointerSize = data.PointerSize,
					EventDataLength = data.EventDataLength
				};

				// Populate Proc name if undefined
				if (String.IsNullOrEmpty(eRecord.ProcessName))
				{
					try
					{
						eRecord.ProcessName = Process.GetProcessById(eRecord.ProcessID).ProcessName;
					}
					catch
					{
						eRecord.ProcessName = "N/A";
					}
				}
				var EventProperties = new Hashtable();

				// Try to parse event XML
				try
				{
					StringReader XmlStringContent = new StringReader(data.ToString());
					XmlTextReader EventElementReader = new XmlTextReader(XmlStringContent);
					while (EventElementReader.Read())
					{
						for (int AttribIndex = 0; AttribIndex < EventElementReader.AttributeCount; AttribIndex++)
						{
							EventElementReader.MoveToAttribute(AttribIndex);

							// Cap maxlen for eventdata elements to 10k
							if (EventElementReader.Value.Length > 10000)
							{
								String DataValue = EventElementReader.Value.Substring(0, Math.Min(EventElementReader.Value.Length, 10000));
								EventProperties.Add(EventElementReader.Name, DataValue);
							}
							else
							{
								EventProperties.Add(EventElementReader.Name, EventElementReader.Value);
							}
						}
					}
				}
				catch
				{
					// For debugging (?), never seen this fail
					EventProperties.Add("XmlEventParsing", "false");
				}
				eRecord.XmlEventData = EventProperties;

				string jsonRecord = "";
				if (collector.ProviderName == "Microsoft-Windows-DNSServer")
				{
					var (dnsRecord, err) = RecordsMapper.MapDnsServerRecord(eRecord);
					if (err != "")
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, err, true);
						return;
					}

					if (dnsRecord.LookupType != "QUERY_RECEIVED") return;

					jsonRecord = Newtonsoft.Json.JsonConvert.SerializeObject(dnsRecord);
				}

				// Serialize to JSON
				String JSONEventData = jsonRecord == ""
					? Newtonsoft.Json.JsonConvert.SerializeObject(eRecord)
					: jsonRecord;

				var syslog = new SysLogClient("logger.ews.lan", 514);
				
				int ProcessResult = ProcessJSONEventData(JSONEventData, collector.OutputType, collector.Path);

				// Verify that we processed the result successfully
				if (ProcessResult != 0)
				{
					if (ProcessResult == 1)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed to write to file", true);
					}
					else if (ProcessResult == 2)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed to POST the result", true);
					}
					else
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed write to the eventlog", true);
					}

					// Write status to eventlog if dictated by the output type
					if (collector.OutputType == OutputType.eventlog)
					{
						WriteEventLogEntry($"{{\"Collector\":\"Stop\",\"Error\":true,\"ErrorCode\":{ProcessResult}}}", EventLogEntryType.Error, EventIds.StopError, collector.Path);
					}

					// This collector encountered an error, terminate the service
					TerminateCollector(EventSource, collector.CollectorGUID, TraceSession);
				}
			}
		};
	}

	static int ProcessJSONEventData(String JSONData, OutputType OutputType, String Path)
	{
		//--[Return Codes]
		// 0 == OK
		// 1 == File write failed
		// 2 == URL POST request failed
		// 3 == Eventlog write failed
		//--

		// Process JSON
		if (OutputType == OutputType.file)
		{
			try
			{
				if (!File.Exists(Path))
				{
					File.WriteAllText(Path, (JSONData + Environment.NewLine));
				}
				else
				{
					File.AppendAllText(Path, (JSONData + Environment.NewLine));
				}

				var syslog = new SysLogClient("logger.ews.lan", 514);
				syslog.Send(JSONData, "SilkETWService", facility: 1, severity: 6);
				syslog.Close();

				return 0;
			}
			catch
			{
				return 1;
			}
		}
		else if (OutputType == OutputType.url)
		{
			try
			{
				string responseFromServer = string.Empty;
				HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(Path);
				webRequest.Timeout = 10000; // 10 second timeout
				webRequest.Method = "POST";
				webRequest.ContentType = "application/json";
				webRequest.Accept = "application/json";
				using (var streamWriter = new StreamWriter(webRequest.GetRequestStream()))
				{
					streamWriter.Write(JSONData);
					streamWriter.Flush();
					streamWriter.Close();
				}
				var httpResponse = (HttpWebResponse)webRequest.GetResponse();
				using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
				{
					var result = streamReader.ReadToEnd();
				}

				return 0;
			}
			catch
			{
				return 2;
			}

		}
		else
		{
			Boolean WriteEvent = WriteEventLogEntry(JSONData, EventLogEntryType.Information, EventIds.Event, Path);

			if (WriteEvent)
			{
				return 0;
			}
			else
			{
				return 3;
			}
		}

		// check if syslog enabled
		// todo: get host/port from config
	}

	static void RetargetEventSource(String LegacySource)
	{
		// This is a fix for: https://github.com/fireeye/SilkETW/issues/4
		// When both SilkETW and SilkService are used on the same host
		// eventlog logging would fail for one or the other as they had
		// the same source. This function will retarget the source.
		if (EventLog.SourceExists(LegacySource))
		{
			EventLog.DeleteEventSource(LegacySource);
		}
	}

	static Boolean WriteEventLogEntry(String Message, EventLogEntryType Type, EventIds EventId, String Path)
	{
		//--[Event ID's]
		// 0 == Collector start
		// 1 == Collector terminated -> by user
		// 2 == Collector terminated -> by error
		// 3 == Event recorded
		//--

		try
		{
			// Fix legacy collector source
			RetargetEventSource("ETW Collector");

			// Event log properties
			String Source = "SilkService Collector";

			// If the source doesn't exist we have to create it first
			if (!EventLog.SourceExists(Source))
			{
				EventLog.CreateEventSource(Source, Path);
			}

			// Write event
			using (EventLog Log = new EventLog(Path))
			{
				Log.Source = Source;
				Log.MaximumKilobytes = 99968; // Max ~100mb size -> needs 64kb increments
				Log.ModifyOverflowPolicy(OverflowAction.OverwriteAsNeeded, 10); // Always overwrite oldest
				Log.WriteEntry(Message, Type, (int)EventId);
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void TerminateCollector(ETWTraceEventSource es, Guid collectorGuid, TraceEventSession ts)
	{
		es.StopProcessing();
		ts?.Stop();
		SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collectorGuid, "Collector terminated", false);
	}

}