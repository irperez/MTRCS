using MTRCS;

var result = CliParser.Parse(args);

if (result.ShouldExit)
{
    Console.WriteLine(result.Message);
    return result.ExitCode;
}

return await new MTRCS.MtrCommand().RunAsync(result.Settings!, CancellationToken.None);
