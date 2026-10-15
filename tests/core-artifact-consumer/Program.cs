using Lazily;

var ctx = new Context();
var source = ctx.Source(1);
var computed = ctx.Computed<int>(c => source.Get(c) * 2);
var runs = 0;
var scope = ctx.Scope();
scope.Own(ctx.Effect(c =>
{
    if (computed.Get(c) < 2) throw new Exception("computed value was not observable");
    runs++;
    return null;
}));
ctx.Batch(() =>
{
    source.Set(2);
    source.Set(3);
});
if (computed.Get() != 6 || runs != 2) throw new Exception($"batch result = {computed.Get()}, runs = {runs}");
scope.Close();
source.Set(4);
if (runs != 2) throw new Exception("closed scope still observed updates");
var typed = ctx.Source(new ConsumerSchema("typed"));
if (typed.Get().Label != "typed") throw new Exception("typed context value was not preserved");
Console.WriteLine("lazily-cs artifact consumer: OK");

internal sealed record ConsumerSchema(string Label);
