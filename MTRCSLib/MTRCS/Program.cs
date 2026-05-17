using Spectre.Console.Cli;

var app = new CommandApp<MTRCS.MtrCommand>();
app.Configure(config =>
{
    config.SetApplicationName("mtrcs");
    config.SetApplicationVersion("1.0.0");
    config.AddExample("example.com");
    config.AddExample("8.8.8.8", "--max-hops", "20", "--interval", "500");
});
return await app.RunAsync(args);
