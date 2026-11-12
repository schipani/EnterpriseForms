using EnterpriseForms.Experiments.ManagedHsmProbe;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await ProbeApplication.RunAsync(args, Console.Out, Console.Error, new AzureHsmClientFactory(), cancellation.Token).ConfigureAwait(false);
