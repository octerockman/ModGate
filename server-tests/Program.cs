using System.IO.Compression;
using ModGate;
using Mono.Cecil;
var passed=0;
void Check(bool value){if(!value)throw new Exception("Assertion failed");}
void Test(string name,Action body){body();passed++;Console.WriteLine("PASS "+name);}
string NewRoot(){var r=Path.Combine(Path.GetTempPath(),"ModGate-server-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(r);return r;}
byte[] Plugin(string guid,string version)
{
 using var a=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture_"+guid,new Version(version)),"Fixture",ModuleKind.Dll);
 var m=a.MainModule;var t=new TypeDefinition("Fixture","Plugin",TypeAttributes.Public|TypeAttributes.Class,m.TypeSystem.Object);m.Types.Add(t);
 var attrType=new TypeReference("BepInEx","BepInPlugin",m,new AssemblyNameReference("BepInEx",new Version(5,4,23,0)));
 var ctor=new MethodReference(".ctor",m.TypeSystem.Void,attrType){HasThis=true};for(int i=0;i<3;i++)ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
 var attr=new CustomAttribute(ctor);foreach(var s in new[]{guid,guid,version})attr.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,s));t.CustomAttributes.Add(attr);
 using var output=new MemoryStream();a.Write(output);return output.ToArray();
}
byte[] Zip(string guid,string version,bool invalid=false,bool patcher=false)
{
 using var output=new MemoryStream();using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
 {
  using(var f=zip.CreateEntry("Mod.dll").Open()){var bytes=Plugin(invalid?"other":guid,version);f.Write(bytes);}
  if(patcher)using(var f=zip.CreateEntry("BepInEx/patchers/unsupported.dll").Open())f.WriteByte(1);
 }
 return output.ToArray();
}
(string root,ServerManifest settings,FakeSource source) Setup(bool lib=false)
{
 var root=NewRoot();var settings=new ServerManifest{AutoUpdateOnRestart=true,AutoUpdateAllMods=true};var source=new FakeSource();
 foreach(var key in lib?new[]{"Mod","Lib"}:new[]{"Mod"})
 {
  var id="A-"+key+"-1.0.0";settings.Mods.Add(new ModEntry{Guid=key,Name=key,Version="1.0.0",Side="required",Package=id});
  var dir=Path.Combine(root,"plugins",key);Directory.CreateDirectory(dir);File.WriteAllBytes(Path.Combine(dir,"Mod.dll"),Plugin(key,"1.0.0"));
  source.New[id]="A-"+key+"-2.0.0";source.Bytes["A-"+key+"-2.0.0"]=Zip(key,"2.0.0");source.Dependencies["A-"+key+"-2.0.0"]=Array.Empty<string>();
 }
 Json.Save(Path.Combine(root,"config","ModGate","server-mods.json"),settings,true);return(root,settings,source);
}
ServerUpdateReport Run(string root,FakeSource source,Action<int>? fault=null)=>ServerUpdater.Run(root,true,source,_=>{},CancellationToken.None,fault).GetAwaiter().GetResult();
void Save(string root,ServerManifest settings)=>Json.Save(Path.Combine(root,"config","ModGate","server-mods.json"),settings,true);
string VersionAt(string root,string key)=>ServerUpdater.Inspect(Path.Combine(root,"plugins",key)).Single().Version;
Test("server auto updates are off by default and individual flags respect master",()=>
{
 var s=new ServerManifest();var m=new ModEntry{Guid="a",AutoUpdate=true};Check(!ServerUpdater.Enabled(s,m));s.AutoUpdateOnRestart=true;Check(ServerUpdater.Enabled(s,m));m.AutoUpdate=false;s.AutoUpdateAllMods=true;Check(!ServerUpdater.Enabled(s,m));m.AutoUpdate=null;Check(ServerUpdater.Enabled(s,m));
});
Test("only dedicated server startup is eligible",()=>{Check(ServerUpdater.IsDedicated("valheim_server",Array.Empty<string>()));Check(!ServerUpdater.IsDedicated("valheim",new[]{"-force-vulkan"}));});
Test("disabled master performs zero repository calls",()=>{var(r,s,f)=Setup();s.AutoUpdateOnRestart=false;Save(r,s);Check(Run(r,f).Status=="Disabled"&&f.Calls==0&&VersionAt(r,"Mod")=="1.0.0");});
Test("per-mod exclusion performs zero repository calls",()=>{var(r,s,f)=Setup();s.Mods[0].AutoUpdate=false;Save(r,s);Run(r,f);Check(f.Calls==0&&VersionAt(r,"Mod")=="1.0.0");});
Test("Nexus entries remain manual with an update report",()=>{var(r,s,f)=Setup();s.Mods[0].NexusUrl="https://www.nexusmods.com/valheim/mods/123";Save(r,s);var report=Run(r,f);Check(f.Calls==0&&report.Entries.Any(e=>e.Contains("manual Nexus")));});
Test("latest package swaps before load while preserving configs and backup",()=>
{
 var(r,s,f)=Setup();f.Dependencies["A-Mod-2.0.0"]=new[]{"denikson-BepInExPack_Valheim-5.4.9999"};File.WriteAllText(Path.Combine(r,"config","Mod.cfg"),"keep-config");
 var report=Run(r,f);Check(report.Status=="Updated 1 packages"&&VersionAt(r,"Mod")=="2.0.0"&&File.ReadAllText(Path.Combine(r,"config","Mod.cfg"))=="keep-config");
 var updated=Json.Read<ServerManifest>(File.ReadAllText(Path.Combine(r,"config","ModGate","server-mods.json")));Check(updated.Mods[0].Package=="A-Mod-2.0.0"&&updated.Mods[0].Version=="2.0.0");Check(Directory.GetFiles(Path.Combine(r,"ModGate","server-updates"),"Mod.dll",SearchOption.AllDirectories).Length>0);
});
Test("missing new dependencies warn without blocking an authorized update or adding another download",()=>{var(r,s,f)=Setup();f.Dependencies["A-Mod-2.0.0"]=new[]{"A-Missing-1.0.0"};var report=Run(r,f);Check(f.Downloads==1&&VersionAt(r,"Mod")=="2.0.0"&&report.Entries.Any(e=>e.Contains("A-Missing-1.0.0")&&e.Contains("warning")));});
Test("disabled incompatible dependency warns without blocking an authorized update or fetching the dependency",()=>{var(r,s,f)=Setup(true);s.Mods.Single(m=>m.Guid=="Lib").AutoUpdate=false;Save(r,s);f.Dependencies["A-Mod-2.0.0"]=new[]{"A-Lib-2.0.0"};var report=Run(r,f);Check(f.Downloads==1&&VersionAt(r,"Mod")=="2.0.0"&&VersionAt(r,"Lib")=="1.0.0"&&report.Entries.Any(e=>e.Contains("warning")&&e.Contains("A-Lib-2.0.0")));});
Test("enabled dependencies update in the same transaction",()=>{var(r,s,f)=Setup(true);f.Dependencies["A-Mod-2.0.0"]=new[]{"A-Lib-2.0.0"};var report=Run(r,f);Check(report.Status=="Updated 2 packages"&&VersionAt(r,"Mod")=="2.0.0"&&VersionAt(r,"Lib")=="2.0.0");});
Test("wrong plugin identity leaves existing server untouched",()=>{var(r,s,f)=Setup();f.Bytes["A-Mod-2.0.0"]=Zip("Mod","2.0.0",true);Run(r,f);Check(VersionAt(r,"Mod")=="1.0.0");});
Test("patcher and loader packages are not automatically installed",()=>{var(r,s,f)=Setup();f.Bytes["A-Mod-2.0.0"]=Zip("Mod","2.0.0",false,true);Run(r,f);Check(VersionAt(r,"Mod")=="1.0.0");});
for(int fault=0;fault<4;fault++){var failAt=fault;Test("server update rolls back every folder move "+fault,()=>{var(r,s,f)=Setup(true);Run(r,f,index=>{if(index==failAt)throw new IOException("test interruption");});Check(VersionAt(r,"Mod")=="1.0.0"&&VersionAt(r,"Lib")=="1.0.0"&&!File.Exists(Path.Combine(r,"ModGate","server-updates","journal.json")));});}
Test("unfinished server transaction recovers on the next startup",()=>
{
 var(r,s,f)=Setup();var batch="ModGate/server-updates/recovery";var target=Path.Combine(r,"plugins","Mod");var backup=SafePath.Under(r,batch+"/backup/Mod");Directory.CreateDirectory(Path.GetDirectoryName(backup)!);Directory.Move(target,backup);Directory.CreateDirectory(target);File.WriteAllBytes(Path.Combine(target,"Mod.dll"),Plugin("Mod","2.0.0"));
 var manifestBackup=SafePath.Under(r,batch+"/before.json");File.Copy(Path.Combine(r,"config","ModGate","server-mods.json"),manifestBackup);
 Json.Save(SafePath.Under(r,"ModGate/server-updates/journal.json"),new ServerUpdateJournal{ManifestBackup=batch+"/before.json",Moves=new List<ServerUpdateMove>{new ServerUpdateMove{Target="plugins/Mod",Prepared=batch+"/prepared/Mod",Backup=batch+"/backup/Mod"}}});ServerUpdater.Recover(r);Check(VersionAt(r,"Mod")=="1.0.0");
});
Test("server updater preserves protected settings and avoids repeating an applied update",()=>
{
 var(r,s,f)=Setup();var m=s.Mods[0];m.DownloadUrl="https://thunderstore.io/package/download/A/Mod/1.0.0/";m.Side="optional";m.Automatic=false;m.AutoUpdate=true;Save(r,s);
 Run(r,f);var updated=Json.Read<ServerManifest>(File.ReadAllText(Path.Combine(r,"config","ModGate","server-mods.json")));var next=updated.Mods.Single();
 Check(next.DownloadUrl==m.DownloadUrl&&next.NexusUrl==m.NexusUrl&&next.AutoUpdate==m.AutoUpdate&&next.Automatic==m.Automatic&&next.Side==m.Side&&next.Version=="2.0.0"&&next.Package=="A-Mod-2.0.0");
 var published=PolicyEngine.Publish(updated,ServerUpdater.Inspect(Path.Combine(r,"plugins")));Check(published.Mods.First(m=>m.Guid=="Mod").Package=="A-Mod-2.0.0");
 f.New["A-Mod-2.0.0"]="A-Mod-2.0.0";var downloads=f.Downloads;Run(r,f);Check(f.Downloads==downloads);
});
Test("server update ignores markers for identities and keeps a locked custom name",()=>
{
 var(r,s,f)=Setup();var mod=s.Mods[0];mod.Guid="M*od";mod.Name="Custom* Name";mod.Side="required*";mod.DownloadUrl="https://thunderstore.io/package/download/A/Mod/1.0.0/*";Save(r,s);
 var report=Run(r,f);var next=Json.Read<ServerManifest>(File.ReadAllText(Path.Combine(r,"config","ModGate","server-mods.json"))).Mods.Single();
 Check(report.Status=="Updated 1 packages"&&VersionAt(r,"Mod")=="2.0.0"&&next.Name=="Custom* Name"&&next.Guid=="M*od"&&next.Version=="2.0.0"&&next.Package=="A-Mod-2.0.0"&&next.DownloadUrl==mod.DownloadUrl&&next.Side==mod.Side);
});
foreach(var field in new[]{"version","package"})Test("locked "+field+" prevents an incompatible physical server upgrade",()=>
{
 var(r,s,f)=Setup();if(field=="version")s.Mods[0].Version+="*";else s.Mods[0].Package+="*";Save(r,s);var report=Run(r,f);Check(f.Calls==0&&f.Downloads==0&&VersionAt(r,"Mod")=="1.0.0"&&report.Entries.Any(e=>e.Contains("locked with *")));
});
Test("server updates retain one DLL identity when legacy config aliases differ",()=>
{
 var(r,s,f)=Setup();s.Mods.Add(new ModEntry{Guid="legacy.alias",Name="Mod",Version="0.0.1",Automatic=false,Side="required",Package="A-Mod-1.0.0"});Save(r,s);
 var report=Run(r,f);var next=Json.Read<ServerManifest>(File.ReadAllText(Path.Combine(r,"config","ModGate","server-mods.json")));Check(report.Status=="Updated 1 packages"&&next.Mods.Count==1&&next.Mods[0].Guid=="Mod"&&next.Mods[0].Version=="2.0.0");
});
Test("installed clientOnly packages update normally and retain both version allowance settings",()=>
{
 var(r,s,f)=Setup();s.Mods[0].Side="clientOnly";s.Mods[0].AllowNewerVersion=false;s.AllowNewerClientVersions=true;Save(r,s);
 var report=Run(r,f);var next=Json.Read<ServerManifest>(File.ReadAllText(Path.Combine(r,"config","ModGate","server-mods.json")));
 Check(report.Status=="Updated 1 packages"&&VersionAt(r,"Mod")=="2.0.0"&&next.Mods.Single().Side=="clientOnly"&&next.Mods[0].AllowNewerVersion==false&&next.AllowNewerClientVersions);
});
Test("server update report retains inner TLS causes while preserving installed files",()=>
{
 var(r,s,f)=Setup();f.Failure=new System.Net.Http.HttpRequestException("The SSL connection could not be established",new System.Security.Authentication.AuthenticationException("fixture CA trust failure"));
 var report=Run(r,f);Check(report.Status=="No updates applied"&&report.Entries.Any(e=>e.Contains("fixture CA trust failure"))&&VersionAt(r,"Mod")=="1.0.0"&&f.Downloads==0);
});
Test("server updater writes a clean owner config while retaining generated metadata separately",()=>
{
 var(r,s,f)=Setup();s.Mods[0].Dependencies.Add(new PluginDependency{Guid="kg.FindOutdatedStuff",Optional=true});Save(r,s);
 var report=Run(r,f);var path=Path.Combine(r,"config","ModGate","server-mods.json");var raw=File.ReadAllText(path);var restored=ServerConfig.Load(path,r);
 Check(report.Status=="Updated 1 packages"&&!raw.Contains("\"dependencies\"")&&!raw.Contains("kg.FindOutdatedStuff")&&restored.Mods.Count==1&&restored.Mods[0].Version=="2.0.0"&&restored.Mods[0].Dependencies.Single().Optional);
});
ServerPackageSourceTests.Run(Test,Check,NewRoot);
PreloaderIsolationTests.Run(Test);
Console.WriteLine("All "+passed+" server update checks passed.");
internal sealed class FakeSource:IUpdateSource
{
 public readonly Dictionary<string,string> New=new();public readonly Dictionary<string,byte[]> Bytes=new();public readonly Dictionary<string,string[]> Dependencies=new();public int Calls,Downloads;
 public Exception? Failure;
 public Task<PackageId> Latest(PackageId id,CancellationToken token){Calls++;if(Failure!=null)throw Failure;return Task.FromResult(PackageId.Parse(New[id.FullName]));}
 public Task<PackageMetadata> Metadata(PackageId id,CancellationToken token){Calls++;return Task.FromResult(new PackageMetadata{FullName=id.RawFullName,Dependencies=Dependencies[id.FullName].ToList()});}
 public Task Download(PackageId id,string path,CancellationToken token){Downloads++;File.WriteAllBytes(path,Bytes[id.FullName]);return Task.CompletedTask;}
}
