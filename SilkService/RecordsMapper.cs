namespace SilkService;

static class RecordsMapper
{
    public static (DnsServerRecord, string) MapDnsServerRecord(EventRecordStruct eRecord)
    {
        var res = new DnsServerRecord();

        if (!eRecord.XmlEventData.ContainsKey("FormattedMessage"))
        {
            // SilkUtility.ReturnStatusMessage("[>] no formattedMessage field", ConsoleColor.Yellow);
            return (res, "FormattedMessage missing in XmlEventData");
        }

        var formattedMessage = eRecord.XmlEventData["FormattedMessage"].ToString();
        res.Timestamp = eRecord.TimeStamp;
        res.ProcessName = eRecord.ProcessName;
        res.EventName = eRecord.EventName;
        res.InterfaceIp = eRecord.XmlEventData.ContainsKey("InterfaceIP") ? eRecord.XmlEventData["InterfaceIP"]?.ToString() : "N/A";
        res.SourceIp = eRecord.XmlEventData.ContainsKey("Source") ? eRecord.XmlEventData["Source"]?.ToString() : "N/A";
        res.Qname = eRecord.XmlEventData.ContainsKey("QNAME") ? eRecord.XmlEventData["QNAME"]?.ToString() : "N/A";
        var qType = eRecord.XmlEventData.ContainsKey("QTYPE") ? eRecord.XmlEventData["QTYPE"]?.ToString() : "N/A";
        _ = int.TryParse(qType, out res.Qtype);
        res.LookupType = formattedMessage.Contains("QUERY_RECEIVED") ? "QUERY_RECEIVED" : "N/A";

        return (res, "");
    }

    private static readonly IReadOnlyDictionary<int, string> CommonTypes =
    new Dictionary<int, string>
    {
                { 1, "A" },         // IPv4 address
                { 2, "NS" },        // Name server
                { 5, "CNAME" },     // Canonical name (alias)
                { 6, "SOA" },       // Start of authority
                { 12, "PTR" },      // Reverse lookup
                { 15, "MX" },       // Mail exchange
                { 16, "TXT" },      // Text record (SPF, DKIM)
                { 28, "AAAA" },     // IPv6 address
                { 33, "SRV" },      // Service record
                { 35, "NAPTR" },    // Naming authority pointer (VoIP, SIP)
                { 43, "DS" },       // Delegation signer
                { 46, "RRSIG" },    // DNSSEC signature
                { 47, "NSEC" },     // DNSSEC next secure
                { 48, "DNSKEY" },   // DNSSEC public key
                { 255, "ANY" },     // Any record type
                { 257, "CAA" },     // Certificate authority authorization
    };

    public static string GetQueryTypeName(int qtype)
    {
        return CommonTypes.TryGetValue(qtype, out var name)
            ? name
            : $"UNKNOWN({qtype})";
    }
}

public struct DnsServerRecord
{
    public string EventName;
    public DateTime Timestamp;
    public string InterfaceIp;
    public string SourceIp;
    public string Qname;
    public int Qtype;
    public string ProcessName;
    public string LookupType;
}