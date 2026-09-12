from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')
count = s.count('hasher.Update(')
if count != 4:
    raise SystemExit(f'Expected 4 CopyEngine serial BLAKE3 updates, found {count}')
s = s.replace('hasher.Update(', 'hasher.UpdateWithJoin(')
engine.write_text(s, encoding='utf-8')

recovery = Path('dotnet/RepartoCopier.Core/Recovery.cs')
r = recovery.read_text(encoding='utf-8')
count = r.count('hasher.Update(')
if count != 1:
    raise SystemExit(f'Expected 1 Recovery serial BLAKE3 update, found {count}')
r = r.replace('hasher.Update(', 'hasher.UpdateWithJoin(')
recovery.write_text(r, encoding='utf-8')

tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]\n    public async Task DiagnosticsSnapshotMeasuresCopyAndVerificationHotPaths()\n'''
if t.count(anchor) != 1:
    raise SystemExit('BLAKE3 regression anchor mismatch')
new_test = r'''    [TestMethod]
    public void ParallelBlake3IncrementalMatchesSerialExactly()
    {
        var payload = new byte[17 * 1024 * 1024 + 997];
        new Random(271828).NextBytes(payload);
        using var serial = Hasher.New();
        using var parallel = Hasher.New();
        const int step = 4 * 1024 * 1024;
        for (var offset = 0; offset < payload.Length; offset += step)
        {
            var length = Math.Min(step, payload.Length - offset);
            var span = payload.AsSpan(offset, length);
            serial.Update(span);
            parallel.UpdateWithJoin(span);
        }
        CollectionAssert.AreEqual(
            serial.Finalize().AsSpan().ToArray(),
            parallel.Finalize().AsSpan().ToArray());
    }

'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')

stress = Path('.github/hash-stress')
stress.mkdir(parents=True, exist_ok=True)
(stress/'HashStress.csproj').write_text(r'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="../../dotnet/RepartoCopier.Core/RepartoCopier.Core.csproj" /></ItemGroup></Project>''', encoding='utf-8')
(stress/'Program.cs').write_text(r'''using System.Diagnostics;
using System.Text.Json;
using RepartoCopier.Core;
static async Task Make(string path,long bytes,int seed){var rnd=new Random(seed);var b=new byte[4*1024*1024];await using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.Asynchronous|FileOptions.SequentialScan);long n=0;while(n<bytes){rnd.NextBytes(b);var c=(int)Math.Min(b.Length,bytes-n);await f.WriteAsync(b.AsMemory(0,c));n+=c;}}
static void Check(CopyJob j){foreach(var s in j.Snapshot())if(s.Phase!=DestinationPhase.Done)throw new Exception($"{s.Phase}: {s.Error}");}
static async Task Run(string name,long bytes,int dests,bool verify){var root=Path.Combine(Path.GetTempPath(),"RC-HashStress",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{var src=Directory.CreateDirectory(Path.Combine(root,"src")).FullName;await Make(Path.Combine(src,"x.bin"),bytes,42+dests);var ds=Enumerable.Range(0,dests).Select(i=>Directory.CreateDirectory(Path.Combine(root,$"d{i}")).FullName).ToArray();var sw=Stopwatch.StartNew();await using var job=CopyEngine.Start(CopyPlan.Create(src,ds,false,false),new CopyOptions(verify,false,false));await job.Completion.WaitAsync(TimeSpan.FromMinutes(5));sw.Stop();Check(job);Console.WriteLine($"HASHSTRESS|{name}|wall_ms={sw.Elapsed.TotalMilliseconds:F0}|{JsonSerializer.Serialize(job.DiagnosticsSnapshot())}");}finally{try{Directory.Delete(root,true);}catch{}}}
await Run("large-single-join",512L*1024*1024,1,false);
await Run("verify-fanout-join",256L*1024*1024,4,true);
''', encoding='utf-8')
