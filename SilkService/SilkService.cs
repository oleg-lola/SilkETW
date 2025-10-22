using Microsoft.Diagnostics.Tracing.Session;
using SyslogNet.Client.Transport;

namespace SilkService;

public class SilkService : IHostedService
{
    private readonly List<Task> CollectorTasks = [];
    // private SysLogClient _sysLogClient;
    private ISyslogMessageSender _sysLogClient;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Service started in console mode");
        // Debugger.Break();
        SilkUtility.WriteToServiceTextLog("[+] SilkService started at: " + DateTime.Now);
        List<CollectorParameters> CollectorConfig = SilkParameters.ReadXmlConfig();
        if (CollectorConfig.Count == 0)
        {
            // We didn't find any ETWCollector elements so we stop the service
            // Logs in ServiceLog text file
            return Task.CompletedTask;
        }

        bool IsSuccess = SilkParameters.ValidateCollectorParameters(CollectorConfig);
        if (!IsSuccess)
        {
            // There was an error in parsing the collector parameters so we stop the service
            // Logs in ServiceLog text file
            // Stop -> OnStop -> Change service state
            return Task.CompletedTask;
        }

        // Check if the config has 1+ Kernel collectors
        // Check if multiple collectors are writing to the same file
        int KCCount = 0;
        bool IsSamePath = false;
        HashSet<string> CCPath = [];
        for (int i = 0; i < CollectorConfig.Count; i++)
        {
            if (CollectorConfig[i].CollectorType == CollectorType.Kernel)
            {
                KCCount += 1;
            }

            if (CollectorConfig[i].OutputType == OutputType.file)
            {
                if (!CCPath.Add(CollectorConfig[i].Path))
                {
                    IsSamePath = true;
                }
            }
        }

        if (KCCount > 1 | IsSamePath)
        {
            if (KCCount > 1)
            {
                SilkUtility.WriteToServiceTextLog("[!] SilkService can only support one Kernel collector..");
            }
            else
            {
                SilkUtility.WriteToServiceTextLog("[!] File based output paths must be unique..");
            }

            return Task.CompletedTask;
        }

        var cc = CollectorConfig.First(c => !string.IsNullOrEmpty(c.SysLogPath));
        var sysLogParts = cc.SysLogPath.Split(':');
        _sysLogClient = new SyslogUdpSender(sysLogParts[1], Convert.ToInt32(sysLogParts[2]));
        SilkUtility.WriteToServiceTextLog($"Syslog client initialized to {sysLogParts[1]}:{sysLogParts[2]}.");

        // We spin up the collector threads
        SilkUtility.WriteToServiceTextLog("[*] Starting collector threads: " + DateTime.Now);
        foreach (CollectorParameters Collector in CollectorConfig)
        {
            // We create a thread for the collector
            CollectorTasks.Add(Task.Factory.StartNew(() =>
            {
                try
                {
                    SilkUtility.WriteToServiceTextLog("    [+] GUID:     " + Collector.CollectorGUID);
                    SilkUtility.WriteToServiceTextLog("    [>] Type:     " + Collector.CollectorType);
                    if (Collector.CollectorType == CollectorType.User)
                    {
                        SilkUtility.WriteToServiceTextLog("    [>] Provider: " + Collector.ProviderName);
                    }
                    else
                    {
                        SilkUtility.WriteToServiceTextLog("    [>] Provider: " + Collector.KernelKeywords);
                    }
                    SilkUtility.WriteToServiceTextLog("    [>] Out Type: " + Collector.OutputType);
                    ETWCollector.StartTrace(Collector, _sysLogClient);
                }
                catch (Exception ex) { SilkUtility.WriteToServiceTextLog("[!] " + ex.ToString()); throw; }
            }));

            // We wait for the thread to signal and then reset the event
            SilkUtility.SignalThreadStarted.WaitOne();
            SilkUtility.SignalThreadStarted.Reset();
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sysLogClient.Dispose();
        // Check if any collector tasks are registered
        if (SilkUtility.CollectorTaskList.Count != 0)
        {
            // We pop terminated threads out of the list
            foreach (CollectorInstance CollectorTask in SilkUtility.CollectorTaskList)
            {
                try
                {
                    CollectorTask.EventSource.StopProcessing();
                    var tes = TraceEventSession.GetActiveSession(CollectorTask.EventParseSessionName);
                    SilkUtility.WriteToServiceTextLog($"Disposing traceEventSession {tes.SessionName}.");
                    tes.Stop();
                    tes.Dispose();
                    SilkUtility.CollectorTaskList.Remove(CollectorTask);
                    SilkUtility.WriteCollectorGuidMessageToServiceTextLog(CollectorTask.CollectorGUID, "Collector terminated", false);
                }
                catch { }
            }
        }

        // Write status to log
        SilkUtility.WriteToServiceTextLog("[+] SilkService stopped at: " + DateTime.Now);

        try
        {
            await Task.WhenAll();
        }
        catch { }
    }
}
