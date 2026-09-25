using MapWright.Ai;
using MapWright.Cli;

using var http = new HttpClient { Timeout = AiProviders.Timeout(Environment.GetEnvironmentVariable) };

IAiProvider? ai;
try
{
    ai = AiProviders.FromEnvironment(Environment.GetEnvironmentVariable, http);
}
catch (AiProviderException ex)
{
    Console.Error.WriteLine($"{ex.Message} AI is disabled.");
    ai = null;
}

return CliApp.Run(args, Console.In, Console.Out, Console.Error, ai);
