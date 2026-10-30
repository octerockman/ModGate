using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ModGate;

internal static partial class Program
{
    private static async Task Product030Checks()
    {
        Test("password memory defaults on and Windows ciphertext round trips without plain text", () =>
        {
            var root = Root(); var vault = new PasswordVault(root);
            Check(vault.Preferences().Enabled);
            if (!PasswordVault.Supported) return;
            vault.Save("server-a", "fixture-密碼-030");
            Check(vault.Read("server-a") == "fixture-密碼-030");
            var file = Directory.GetFiles(Path.Combine(root, "ModGate/private-passwords")).Single();
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("fixture-密碼"));
            vault.Enable("server-a", false); Check(!vault.Has("server-a") && vault.Read("server-a") == null);
        });
        Test("password entries are bound to the server identity and disabling the keeper clears entries", () =>
        {
            if (!PasswordVault.Supported) return;
            var root = Root(); var vault = new PasswordVault(root); vault.Save("a", "fixture-one");
            var first = Directory.GetFiles(Path.Combine(root, "ModGate/private-passwords")).Single();
            vault.Save("b", "fixture-two");
            var second = Directory.GetFiles(Path.Combine(root, "ModGate/private-passwords")).Single(f => f != first);
            File.Copy(first, second, true); Check(vault.Read("b") == null && vault.Read("a") == "fixture-one");
            vault.EnableAll(false); Check(!vault.Has("a") && !vault.Enabled("a"));
        });
        Test("asterisk locks also survive new dependency metadata normalization",()=>
        {
            var original=Mod("app");original.Dependencies.Add(new PluginDependency{Guid="li*b",MinimumVersion="2.0*.0",Package="A-Lib-1.0.0*"});
            var settings=new ServerManifest{Mods=new(){original}};var effective=ConfigFields.Runtime(settings);
            Check(effective.Mods[0].Dependencies[0].Guid=="lib"&&effective.Mods[0].Dependencies[0].MinimumVersion=="2.0.0"&&!Json.Write(effective).Contains('*'));
            effective.Mods[0].Dependencies[0].MinimumVersion="3.0.0";effective.Mods[0].Dependencies[0].Package="A-Lib-2.0.0";ConfigFields.Preserve(settings,effective);
            Check(effective.Mods[0].Dependencies[0].MinimumVersion=="2.0*.0"&&effective.Mods[0].Dependencies[0].Package=="A-Lib-1.0.0*");
        });
        var app = Mod("app", package:"A-App-1.0.0"); var module = Mod("module", "optional", package:"A-Module-1.0.0");
        app.Dependencies.Add(new PluginDependency { Guid=module.Guid, Optional=true, Package=module.Package });
        var server = Policy(app, module); server.AllowExtras = true;
        var source = new ProductSource(); source.MetadataEntries[app.Package] = new PackageMetadata { FullName=app.Package, Dependencies=new(){module.Package} };
        source.MetadataEntries[module.Package] = new PackageMetadata { FullName=module.Package };
        var plan = ProfileSelection.Build(server, Array.Empty<ModEntry>(), new HashSet<string>());
        var diff = new Comparison(); diff.Missing.AddRange(plan.Downloads);
        var result = await Downloads.Resolve(ProfileSelection.ForSelection(server,new HashSet<string>()), diff, source, CancellationToken.None);
        Test("unchecked soft DLL modules are omitted despite package dependencies", () => Check(result.Single().FullName==app.Package && !source.MetadataRequests.Contains(module.Package)));
        var chosen = new HashSet<string>{module.Guid}; plan=ProfileSelection.Build(server, Array.Empty<ModEntry>(), chosen); diff=new Comparison();diff.Missing.AddRange(plan.Downloads);
        result=await Downloads.Resolve(ProfileSelection.ForSelection(server,chosen),diff,source,CancellationToken.None);
        Test("selected optional modules resolve and are included in the profile",()=>Check(result.Count==2));
        app.Dependencies[0].Optional=false;
        plan=ProfileSelection.Build(server,Array.Empty<ModEntry>(),new HashSet<string>());
        Test("hard DLL dependencies remain opt-in when the server marks the plugin optional",()=>Check(plan.Downloads.Count==1&&!DependencyRules.Required(server,new HashSet<string>()).Contains(module.Guid)));
        app.Dependencies[0].Optional=true; module.Side="required";
        plan=ProfileSelection.Build(server,Array.Empty<ModEntry>(),new HashSet<string>());
        Test("explicit server-required modules remain mandatory even when DLL dependency is soft",()=>Check(plan.Downloads.Count==2));
        module.Side="optional";
        var local=Mod("app",package:app.Package);local.LocalFile=Put(Root(),"plugins/App/App.dll","fixture");
        source.MetadataEntries[app.Package].Dependencies=new(){"A-Library-1.0.0"};source.MetadataEntries["A-Library-1.0.0"]=new PackageMetadata{FullName="A-Library-1.0.0"};
        plan=ProfileSelection.Build(server,new[]{local},new HashSet<string>());diff=new Comparison();diff.Missing.AddRange(plan.Downloads);
        result=await Downloads.Resolve(ProfileSelection.ForSelection(server,new HashSet<string>()),diff,source,CancellationToken.None,plan.Local);
        Test("reused installed roots do not download unchecked package dependency suggestions",()=>Check(result.Count==0));
        var issues=new List<string>();result=await PartialSetup.Resolve(ProfileSelection.ForSelection(server,new HashSet<string>()),diff,source,CancellationToken.None,plan.Local,issues);
        Test("override also keeps dependencies of reused roots opt-in",()=>Check(result.Count==0&&issues.Any(i=>i.Contains("not downloaded"))));
        var detected=Mod("detector",package:"ValheimModding-JsonDotNET-13.0.4");detected.LocalManifest=new PackageManifest{Name="JsonDotNET",Version="13.0.4"};
        var settings=AutoSetup.Reconcile(new ServerManifest{Mods=new(){detected}},new[]{detected},Array.Empty<string>());
        Test("DLL version and JsonDotNET package version stay separate",()=>Check(settings.Mods.Single().Version=="1.0.0"&&settings.Mods[0].Package.EndsWith("13.0.4")));
        var owner=Mod("app");owner.Dependencies.Add(new PluginDependency{Guid="feature",Optional=true});var feature=Mod("feature");
        var discovered=AutoSetup.Reconcile(new ServerManifest(),new[]{owner,feature},Array.Empty<string>());
        Test("new hard and soft dependency modules default optional while existing classifications and exclusions are preserved",()=>
        {
            Check(discovered.Mods.Single(m=>m.Guid=="feature").Side=="optional");
            owner.Dependencies[0].Optional=false;Check(AutoSetup.Reconcile(new ServerManifest(),new[]{owner,feature},Array.Empty<string>()).Mods.Single(m=>m.Guid=="feature").Side=="optional");
            var old=new ServerManifest{Mods=new(){Mod("feature")}};Check(AutoSetup.Reconcile(old,new[]{owner,feature},Array.Empty<string>()).Mods.Single(m=>m.Guid=="feature").Side=="required");
            old.Mods[0].Side="clientOnly*";Check(AutoSetup.Reconcile(old,new[]{owner,feature},Array.Empty<string>()).Mods.Single(m=>m.Guid=="feature").Side=="clientOnly*");
            Check(AutoSetup.Reconcile(new ServerManifest(),new[]{owner,feature},new[]{"feature"}).Mods.Single(m=>m.Guid=="feature").Side=="serverOnly");
        });
        var configRoot=Root();Put(configRoot,"plugins/Original.dll","fixture");Put(configRoot,"config/app.cfg","offline-settings");
        async Task<PendingInstall> StageProfile(string? config)
        {
            var pending=await Downloads.Stage(configRoot,Array.Empty<PackageId>(),source,_=>{},CancellationToken.None);
            Put(configRoot,"ModGate/staging/"+pending.Id+"/packages/A-App/App.dll","fixture");pending.Packages.Add("A-App-1.0.0");
            if(config!=null)Put(configRoot,"ModGate/staging/"+pending.Id+"/config/app.cfg",config);
            ProfileConfigs.RefreshReceipt(SafePath.Under(configRoot,"ModGate/staging/"+pending.Id),pending);return pending;
        }
        SavedProfile Profile(PendingInstall pending)=>new SavedProfile{Slot="server-1",ServerKey=new string('b',64),Name="One",Mods=new(){Mod("app")},Packages=pending.Packages};
        var firstStage=await StageProfile("server-settings");Profiles.Schedule(configRoot,new ProfileCommand{Kind="install",Target="server-1",Install=firstStage,Profile=Profile(firstStage),ReplaceConfig=true});Profiles.Apply(configRoot,_=>{});
        Profiles.Schedule(configRoot,new ProfileCommand{Kind="switch",Target="offline"});Profiles.Apply(configRoot,_=>{});
        var updated=await StageProfile(null);ProfileConfigs.Stage(configRoot,new[]{Mod("app")},"saved:server-1",updated);
        Profiles.Schedule(configRoot,new ProfileCommand{Kind="install",Target="server-1",Install=updated,Profile=Profile(updated),ReplaceConfig=false});Profiles.Apply(configRoot,_=>{});
        Test("rebuilding an inactive server profile preserves its saved configs",()=>Check(File.ReadAllText(SafePath.Under(configRoot,"config/app.cfg"))=="server-settings"));
        updated=await StageProfile("new-settings");Profiles.Schedule(configRoot,new ProfileCommand{Kind="install",Target="server-1",Install=updated,Profile=Profile(updated),ReplaceConfig=true});Profiles.Apply(configRoot,_=>{});
        Test("explicit config replacements retain the old config in backups",()=>Check(File.ReadAllText(SafePath.Under(configRoot,"config/app.cfg"))=="new-settings"&&Directory.GetFiles(SafePath.Under(configRoot,"ModGate/config-backups"),"app.cfg",SearchOption.AllDirectories).Any(f=>File.ReadAllText(f)=="server-settings")));
        var correctedLibrary=Mod("library",version:"2.0.0",package:"A-Library-1.0.0");correctedLibrary.Automatic=true;
        var correctionSource=new ProductSource();correctionSource.MetadataEntries["A-App-1.0.0"]=new PackageMetadata{FullName="A-App-1.0.0",Dependencies=new(){"A-Library-2.0.0"}};
        correctionSource.MetadataEntries["A-Library-1.0.0"]=new PackageMetadata{FullName="A-Library-1.0.0"};
        correctionSource.MetadataEntries["A-Library-2.0.0"]=new PackageMetadata{FullName="A-Library-2.0.0"};
        var correctionPolicy=Policy(Mod("app",package:"A-App-1.0.0"),correctedLibrary);var correctionDiff=new Comparison();correctionDiff.Missing.Add(correctionPolicy.Mods[0]);var correctionIssues=new List<string>();
        var corrected=await Downloads.Resolve(correctionPolicy,correctionDiff,new ProfilePackageSource(correctionSource,_=>{},correctionIssues),CancellationToken.None,warn:correctionIssues.Add);
        Test("dependency suggestions warn about stale automatic mappings without rewriting server pins or DLL requirements",()=>Check(corrected.Any(p=>p.FullName=="A-Library-1.0.0")&&correctedLibrary.Version=="2.0.0"&&correctedLibrary.Package=="A-Library-1.0.0"&&correctionIssues.Any(i=>i.Contains("Setup can continue"))));
        var cacheRoot=Root();source.Archive=Archive(cacheRoot,("App.dll","fixture"));var id=PackageId.Parse("A-App-1.0.0");
        var pendingCache=await Downloads.Stage(cacheRoot,new[]{id},source,_=>{},CancellationToken.None);DownloadCache.DiscardStage(cacheRoot,pendingCache.Id);
        var count=source.DownloadCalls;pendingCache=await Downloads.Stage(cacheRoot,new[]{id},source,_=>{},CancellationToken.None);
        Test("completed downloads are verified and reused for a new staging attempt",()=>Check(source.DownloadCalls==count&&pendingCache.Files.Count==1));
        DownloadCache.DiscardStage(cacheRoot,pendingCache.Id);source.Fail=true;var failed=Guid.NewGuid().ToString("N");
        try{await Downloads.Stage(cacheRoot,new[]{PackageId.Parse("A-Other-1.0.0")},source,_=>{},CancellationToken.None,failed);throw new Exception("Expected failure");}catch(IOException){}
        Test("failed transfers retry and remove their staging folder while retaining resumable cache",()=>Check(!Directory.Exists(SafePath.Under(cacheRoot,"ModGate/staging/"+failed))&&source.DownloadCalls==count+3));
        DownloadCache.Clear(cacheRoot);Test("clear downloads removes only cache and failed staging storage",()=>Check(!Directory.Exists(SafePath.Under(cacheRoot,"ModGate/download-cache"))));
        var bytes=Enumerable.Range(0,200000).Select(n=>(byte)(n%251)).ToArray();var handler=new ResumeHandler(bytes);
        using(var transport=new RepositorySource(handler))
        {
            var file=Path.Combine(Root(),"resume.partial");
            try{await transport.Download(id,file,CancellationToken.None);throw new Exception("Expected interruption");}catch(IOException){}
            await transport.Download(id,file,CancellationToken.None);
            Test("Thunderstore interrupted downloads resume with If-Range and exact bytes",()=>Check(handler.Resumed&&File.ReadAllBytes(file).SequenceEqual(bytes)&&!File.Exists(file+".resume")));
        }
        var hex = new HexResumeHandler();
        using(var transport = new RepositorySource(hex))
        {
            var file = Path.Combine(Root(),"hex.partial");
            try { await transport.Download(PackageId.Parse("hexium:A-App-1.0.0"),file,CancellationToken.None); throw new Exception("Expected interrupted range"); } catch(IOException) { }
            var before = hex.Requests.TryGetValue(524288,out var existing) ? existing : 0;
            await transport.Download(PackageId.Parse("hexium:A-App-1.0.0"),file,CancellationToken.None);
            Test("Hexium resumes only missing chunks with validated entity tags and exact bytes",()=>Check(File.ReadAllBytes(file).SequenceEqual(hex.Bytes)&&hex.Requests[524288]==before&&!File.Exists(file+".resume")));
        }
    }
    private sealed class ProductSource:IPackageSource
    {
        public readonly Dictionary<string,PackageMetadata> MetadataEntries=new();public readonly List<string> MetadataRequests=new();
        public string Archive="";public bool Fail;public int DownloadCalls;
        public Task<PackageMetadata> Metadata(PackageId id,CancellationToken token){MetadataRequests.Add(id.FullName);return Task.FromResult(MetadataEntries[id.FullName]);}
        public Task Download(PackageId id,string file,CancellationToken token){DownloadCalls++;if(Fail){File.WriteAllText(file,"partial");throw new IOException("fixture failure");}File.Copy(Archive,file,true);return Task.CompletedTask;}
    }
    private sealed class ResumeHandler:HttpMessageHandler
    {
        private readonly byte[] bytes;private bool first=true;public bool Resumed;
        public ResumeHandler(byte[] bytes){this.bytes=bytes;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            HttpContent content;HttpStatusCode status;long offset=0;
            if(first){first=false;content=new StreamContent(new InterruptedStream(bytes));content.Headers.ContentLength=bytes.Length;status=HttpStatusCode.OK;}
            else{offset=request.Headers.Range?.Ranges.Single().From??0;Resumed=offset>0&&request.Headers.Contains("If-Range");content=new ByteArrayContent(bytes.Skip((int)offset).ToArray());content.Headers.ContentRange=new ContentRangeHeaderValue(offset,bytes.Length-1,bytes.Length);status=HttpStatusCode.PartialContent;}
            var response=new HttpResponseMessage(status){Content=content};response.Headers.ETag=new EntityTagHeaderValue("\"fixture\"");return Task.FromResult(response);
        }
    }
    private sealed class InterruptedStream:MemoryStream
    {
        private bool read;public InterruptedStream(byte[] bytes):base(bytes){}
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token){if(read)throw new IOException("fixture interrupted");read=true;return base.ReadAsync(buffer,offset,count,token);}
    }
    private sealed class HexResumeHandler:HttpMessageHandler
    {
        public readonly byte[] Bytes=Enumerable.Range(0,1048699).Select(n=>(byte)(n%251)).ToArray();
        public readonly Dictionary<long,int> Requests=new();private int failed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.Host=="hexium.gg")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"full_name\":\"A-App-1.0.0\",\"download_url\":\"https://cdn.hexium.gg/fixture.zip\",\"dependencies\":[]}")});
            var range=request.Headers.Range!.Ranges.Single();var start=range.From!.Value;var end=Math.Min(range.To!.Value,Bytes.Length-1);
            lock(Requests){Requests[start]=Requests.TryGetValue(start,out var count)?count+1:1;}
            if(start==262144&&Interlocked.Exchange(ref failed,1)==0)throw new IOException("fixture range interruption");
            var content=new ByteArrayContent(Bytes.Skip((int)start).Take((int)(end-start+1)).ToArray());content.Headers.ContentRange=new ContentRangeHeaderValue(start,end,Bytes.Length);
            var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=content};response.Headers.ETag=new EntityTagHeaderValue("\"hex-fixture\"");return Task.FromResult(response);
        }
    }
}
