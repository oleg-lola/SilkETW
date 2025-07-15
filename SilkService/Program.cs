using System.Runtime.InteropServices;

var builder = Host.CreateDefaultBuilder(args);
var isWindowsService = !args.Contains("console") && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

builder.ConfigureServices((ctx, svc) =>
{
	svc.AddHostedService<SilkService.SilkService>();

	if (isWindowsService)
	{
		svc.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
	}
});

if (isWindowsService)
{
	builder.UseWindowsService(o => o.ServiceName = "SilkService");
}
else
{
	builder.UseConsoleLifetime();
}

var host = builder.Build();
host.Run();
