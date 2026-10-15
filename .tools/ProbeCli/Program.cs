using Grpc.Net.Client;
using Personalhub;

string addr = args.Length > 0 ? args[0] : "http://127.0.0.1:5000";
string mode = args.Length > 1 ? args[1] : "upload";

using var channel = GrpcChannel.ForAddress(addr);
var sys = new Personalhub.System.SystemClient(channel);
var rec = new RecordService.RecordServiceClient(channel);

var ping = await sys.PingAsync(new PingRequest());
Console.WriteLine($"Ping ok={ping.Ok}");

if (mode == "query")
{
    var today = await rec.GetTodayRecordsAsync(new Empty());
    Console.WriteLine($"今日条数={today.Records.Count}");
    foreach (var r in today.Records)
        Console.WriteLine($"   id={r.Id} amount={r.Amount} cat={r.CategoryId} pay={r.PayMethod} note={r.Note}");

    var trend = await rec.GetExpenseTrendAsync(new TrendRequest { Range = "7d" });
    Console.WriteLine($"趋势 total={trend.Total} avg={trend.DailyAvg} points={trend.Points.Count}");
}
else
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var req = new UploadRecordsRequest();
    req.Records.Add(new Record { Id = "probe-a", Amount = 12.5, CategoryId = 1, PayMethod = "现金", Note = "探针A", SpentAt = now, CreatedAt = now });
    req.Records.Add(new Record { Id = "probe-b", Amount = 30.0, CategoryId = 2, PayMethod = "微信", Note = "探针B", SpentAt = now, CreatedAt = now });

    var resp = await rec.UploadRecordsAsync(req);
    Console.WriteLine($"上传 accepted={resp.AcceptedCount} failed=[{string.Join(",", resp.FailedIds)}]");

    var today = await rec.GetTodayRecordsAsync(new Empty());
    Console.WriteLine($"上传后今日条数={today.Records.Count}");
}
