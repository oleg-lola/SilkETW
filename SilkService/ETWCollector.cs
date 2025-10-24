using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Xml;
using System.Collections;
using System.Net;
using SyslogNet.Client.Transport;
using SyslogNet.Client.Serialization;

namespace SilkService;

class ETWCollector
{
	private static ISyslogMessageSender _sysLogClient;
	private static SyslogRfc5424MessageSerializer _serializer = new SyslogRfc5424MessageSerializer();

	public static void StartTrace(CollectorParameters collector, ISyslogMessageSender sysLogClient)
	{
		_sysLogClient = sysLogClient;

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

				string dnsLogMessage = "";
				if (collector.ProviderName == "Microsoft-Windows-DNSServer")
				{
					var (dnsRecord, err) = RecordsMapper.MapDnsServerRecord(eRecord);
					if (err != "")
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, err, true);
						return;
					}

					if (dnsRecord.LookupType != "QUERY_RECEIVED") return;

					// query: test1.test IN AAAA (1.2.3.4)
					// meta: {"EventName":"LOOK_UP","InterfaceIp":"2.2.2.2","SourceIp":"1.2.3.4","Qname":"test1.test.","LookupType":"QUERY_RECEIVED"}
					// dnsLogMessage = Newtonsoft.Json.JsonConvert.SerializeObject(dnsRecord);

					dnsLogMessage = $"client: SilkETWService, queryTime: {dnsRecord.Timestamp}, query: {dnsRecord.Qname} {RecordsMapper.GetQueryTypeName(dnsRecord.Qtype)} ({dnsRecord.SourceIp})";
				}

				// Serialize to JSON
				string logMessage = dnsLogMessage == ""
					? Newtonsoft.Json.JsonConvert.SerializeObject(eRecord)
					: dnsLogMessage;

				int result = ProcessLogMessage(logMessage, collector.OutputType, collector.Path);

				// Verify that we processed the result successfully
				if (result != 0)
				{
					if (result == 1)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed to write to file", true);
					}
					else if (result == 2)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed to POST the result", true);
					}
					else if (result == 3)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed write to the eventlog", true);
					}
					else if (result == 4)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "The collector failed to publish to syslog", true);
					}
					else if (result == 5)
					{
						SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collector.CollectorGUID, "Unknown output type", true);
					}

					// Write status to eventlog if dictated by the output type
					if (collector.OutputType == OutputType.eventlog)
					{
						WriteEventLogEntry($"{{\"Collector\":\"Stop\",\"Error\":true,\"ErrorCode\":{result}}}", EventLogEntryType.Error, EventIds.StopError, collector.Path);
					}

					// This collector encountered an error, terminate the service
					TerminateCollector(EventSource, collector.CollectorGUID, TraceSession);
				}
			}
		};
	}

	static int ProcessLogMessage(String logMessage, OutputType OutputType, String Path)
	{
		//--[Return Codes]
		// 0 == OK
		// 1 == File write failed
		// 2 == URL POST request failed
		// 3 == Eventlog write failed
		// 4 == Syslog write failed
		// 5 == Unknown output type
		//--

		// Process JSON
		if (OutputType == OutputType.file)
		{
			try
			{
				if (!File.Exists(Path))
				{
					File.WriteAllText(Path, (logMessage + Environment.NewLine));
				}
				else
				{
					File.AppendAllText(Path, (logMessage + Environment.NewLine));
				}

				return 0;
			}
			catch
			{
				return 1;
			}
		}

		if (OutputType == OutputType.url)
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
					streamWriter.Write(logMessage);
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

		if (OutputType == OutputType.eventlog)
		{
			Boolean WriteEvent = WriteEventLogEntry(logMessage, EventLogEntryType.Information, EventIds.Event, Path);

			if (WriteEvent)
			{
				return 0;
			}
			else
			{
				return 3;
			}
		}

		if (OutputType == OutputType.syslog)
		{
			// var meta = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonRecord);
			var dict = new Dictionary<string, string>
			{
				{ "foo", "bar" }
			};
			var sde = new SyslogNet.Client.StructuredDataElement("meta", dict);

			var syslogFullMessage = new SyslogNet.Client.SyslogMessage(
				DateTimeOffset.Now,
				SyslogNet.Client.Facility.UserLevelMessages,
				SyslogNet.Client.Severity.Informational,
				Dns.GetHostName(),
				"SilkETWService", "", "", logMessage, sde
			);

			try
			{
				_sysLogClient.Send(syslogFullMessage, _serializer);
			}
			catch (Exception e)
			{
				SilkUtility.WriteToServiceTextLog($"Error publishing to syslog: {e}");
				return 4;
			}

			return 0;
		}

		SilkUtility.WriteToServiceTextLog("No output type matched for event data processing.");
		return 5;
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
		ts?.Dispose();
		SilkUtility.WriteCollectorGuidMessageToServiceTextLog(collectorGuid, "Collector terminated", false);
	}

}