namespace SilkETW;

static class RecordsMapper
{
    public static (DnsServerRecord, string) MapDnsServerRecord(EventRecordStruct eRecord)
    {
        var res = new DnsServerRecord();

        if (!eRecord.XmlEventData.ContainsKey("FormattedMessage"))
        {
            SilkUtility.ReturnStatusMessage("[>] no formattedMessage field", ConsoleColor.Yellow);
            return (res, "FormattedMessage missing in XmlEventData");
        }

        var formattedMessage = eRecord.XmlEventData["FormattedMessage"].ToString();
        res.Timestamp = eRecord.TimeStamp;
        res.ProcessName = eRecord.ProcessName;
        res.EventName = eRecord.EventName;
        res.InterfaceIp = eRecord.XmlEventData.ContainsKey("InterfaceIP") ? eRecord.XmlEventData["InterfaceIP"].ToString() : "N/A";
        res.SourceIp = eRecord.XmlEventData.ContainsKey("Source") ? eRecord.XmlEventData["Source"].ToString() : "N/A";
        res.Qname = eRecord.XmlEventData.ContainsKey("QNAME") ? eRecord.XmlEventData["QNAME"].ToString() : "N/A";
        res.LookupType = formattedMessage.Contains("QUERY_RECEIVED") ? "QUERY_RECEIVED" : "N/A";

        return (res, "");
    }
}
