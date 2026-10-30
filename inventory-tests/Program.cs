using System.IO.Compression;
using ModGate;
using Mono.Cecil;
var passed=0;
void Check(bool value){if(!value)throw new Exception("Assertion failed");}
void Test(string name,Action body){body();passed++;Console.WriteLine("PASS "+name);}
string NewRoot(){var root=Path.Combine(Path.GetTempPath(),"ModGate-inventory-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
byte[] Plugin(string guid,string version)
{
 using var a=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture_"+guid,new System.Version(version)),"Fixture",ModuleKind.Dll);
 var m=a.MainModule;var t=new TypeDefinition("Fixture","Plugin",TypeAttributes.Public|TypeAttributes.Class,m.TypeSystem.Object);m.Types.Add(t);
 var attrType=new TypeReference("BepInEx","BepInPlugin",m,new AssemblyNameReference("BepInEx",new System.Version(5,4,23,0)));
 var ctor=new MethodReference(".ctor",m.TypeSystem.Void,attrType){HasThis=true};for(int i=0;i<3;i++)ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
 var attr=new CustomAttribute(ctor);foreach(var s in new[]{guid,guid,version})attr.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,s));t.CustomAttributes.Add(attr);
 using var output=new MemoryStream();a.Write(output);return output.ToArray();
}

ModEntry Mod(string guid,string version="1.0.0",string side="required")=>new ModEntry{Guid=guid,Name=guid,Version=version,Side=side};
(string root,PendingInstall pending) Staged(params (string key,string guid,string version)[] mods)
{
 var root=NewRoot();var pending=Downloads.Stage(root,Array.Empty<PackageId>(),new NeverSource(),_=>{},CancellationToken.None).GetAwaiter().GetResult();
 foreach(var mod in mods){var folder=SafePath.Under(root,"ModGate/staging/"+pending.Id+"/packages/"+mod.key);Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,mod.guid+".dll"),Plugin(mod.guid,mod.version));pending.Packages.Add(mod.key+"-"+mod.version);}
 ProfileConfigs.RefreshReceipt(SafePath.Under(root,"ModGate/staging/"+pending.Id),pending);return(root,pending);
}
void Verify(string root,PendingInstall pending)=>StageVerifier.VerifyStage(SafePath.Under(root,"ModGate/staging/"+pending.Id),pending);
Test("partial proposal allows missing mods but server comparison still rejects",()=>
{
 var(r,p)=Staged(("A-Good","good","1.0.0"));var policy=new Policy{Mods=new(){Mod("good"),Mod("missing")}};
 bool rejected=false;try{Inventory.CompleteProfileProposal(r,policy,p);}catch(InvalidDataException){rejected=true;}
 var mods=Inventory.CompleteProfileProposal(r,policy,p,true);Check(rejected&&mods.Any(m=>m.Guid=="good")&&!PolicyEngine.Compare(policy,mods).Matches);
});
Test("override removes server-only bundles without installing their client plugins",()=>
{
 var(r,p)=Staged(("A-Bundle","good","1.0.0"));var folder=SafePath.Under(r,"ModGate/staging/"+p.Id+"/packages/A-Bundle");File.WriteAllBytes(Path.Combine(folder,"admin.dll"),Plugin("admin","1.0.0"));
 var policy=new Policy{Mods=new(){Mod("good")},ForbiddenPluginGuids=new(){"admin"}};var issues=new List<string>();Inventory.FilterPartialStage(r,policy,p,new HashSet<string>(),issues);Verify(r,p);
 Check(p.Packages.Count==0&&p.Files.Count==0&&issues.Single().Contains("server-only"));Check(Inventory.CompleteProfileProposal(r,policy,p,true).Count==1);
});
Test("override respects unchecked optional plugins bundled with a required mod",()=>
{
 var(r,p)=Staged(("A-Bundle","good","1.0.0"));File.WriteAllBytes(SafePath.Under(r,"ModGate/staging/"+p.Id+"/packages/A-Bundle/optional.dll"),Plugin("optional","1.0.0"));var issues=new List<string>();
 Inventory.FilterPartialStage(r,new Policy{Mods=new(){Mod("good"),Mod("optional",side:"optional")}},p,new HashSet<string>(),issues);Verify(r,p);Check(p.Packages.Count==0&&issues.Single().Contains("unchecked"));
});
Test("override removes duplicate plugin bundles and preserves a valid receipt",()=>
{
 var(r,p)=Staged(("A-First","good","1.0.0"),("A-Second","good","1.0.0"));var issues=new List<string>();var policy=new Policy{Mods=new(){Mod("good")}};
 Inventory.FilterPartialStage(r,policy,p,new HashSet<string>(),issues);Verify(r,p);Check(p.Packages.Count==1&&issues.Single().Contains("duplicate"));Check(Inventory.CompleteProfileProposal(r,policy,p,true).Count==2);
});
Test("override cannot replace the connector itself",()=>
{
 var(r,p)=Staged(("A-Connector",Constants.Guid,Constants.Version));var issues=new List<string>();Inventory.FilterPartialStage(r,new Policy(),p,new HashSet<string>(),issues);Verify(r,p);Check(p.Packages.Count==0&&issues.Single().Contains("ModGate itself"));
});
Test("override retains actual wrong versions for honest server checks",()=>
{
 var(r,p)=Staged(("A-Good","good","1.0.0"));var policy=new Policy{Mods=new(){Mod("good","2.0.0")}};var issues=new List<string>();Inventory.FilterPartialStage(r,policy,p,new HashSet<string>(),issues);
 var mods=Inventory.CompleteProfileProposal(r,policy,p,true);Check(mods.Single(m=>m.Guid=="good").Version=="1.0.0"&&PolicyEngine.Compare(policy,mods).WrongVersion.Count==1);
});
Test("partial activation and offline restoration retain original plugin bytes",()=>
{
 var(r,p)=Staged(("A-Good","good","1.0.0"));var original=SafePath.Under(r,"plugins/Original/original.dll");Directory.CreateDirectory(Path.GetDirectoryName(original)!);File.WriteAllBytes(original,Plugin("original","1.0.0"));var before=SafePath.Hash(original);
 var policy=new Policy{Mods=new(){Mod("good"),Mod("missing")}};var mods=Inventory.CompleteProfileProposal(r,policy,p,true);
 Profiles.Schedule(r,new ProfileCommand{Kind="install",Target="server-1",ReplaceConfig=true,Install=p,Profile=new SavedProfile{Slot="server-1",ServerKey=new string('e',64),Name="Partial",Incomplete=true,Mods=mods,Packages=p.Packages}});Profiles.Apply(r,_=>{});
 Check(Profiles.Load(r).Servers.Single().Incomplete);Check(ServerUpdaterFixture(r));Profiles.Schedule(r,new ProfileCommand{Kind="switch",Target="offline"});Profiles.Apply(r,_=>{});Check(SafePath.Hash(original)==before);
});
bool ServerUpdaterFixture(string root){var folder=SafePath.Under(root,"plugins/ValheimModGate.Active");return Directory.GetFiles(folder,"*.dll",SearchOption.AllDirectories).Length==1;}
Test("production profile inspector reads actual DLL versions and toggles a complete bundle",()=>
{
 var r=NewRoot();var folder=SafePath.Under(r,"plugins/Bundle");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"first.dll"),Plugin("first","1.2.3"));File.WriteAllBytes(Path.Combine(folder,"second.dll"),Plugin("second","2.3.4"));File.WriteAllText(Path.Combine(folder,"asset.bundle"),"asset");File.WriteAllText(Path.Combine(folder,"manifest.json"),"{\"name\":\"WrongName\",\"version_number\":\"0.0.1\"}");
 var catalog=Profiles.Load(r);var groups=Inventory.ProfileGroups(r,catalog,"offline");Check(groups.Single().Mods.Count==2&&groups[0].Mods.Single(m=>m.Guid=="first").Version=="1.2.3");
 groups[0].Enabled=false;var command=ProfileEditing.Prepare(r,catalog,"offline",groups,null,new(),new(),CancellationToken.None);Profiles.Schedule(r,command);Profiles.Apply(r,_=>{});
 var hidden=Inventory.ProfileGroups(r,Profiles.Load(r),"offline");Check(!hidden.Single().Enabled&&hidden[0].Mods.Count==2&&!Directory.Exists(folder));
 hidden[0].Enabled=true;Profiles.Schedule(r,ProfileEditing.Prepare(r,Profiles.Load(r),"offline",hidden,null,new(),new(),CancellationToken.None));Profiles.Apply(r,_=>{});Check(File.ReadAllText(Path.Combine(folder,"asset.bundle"))=="asset");
});
Test("production inspector keeps a bundled connector outside the mod editor",()=>
{
 var r=NewRoot();var folder=SafePath.Under(r,"plugins/Shared");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"connector.dll"),Plugin(Constants.Guid,Constants.Version));File.WriteAllBytes(Path.Combine(folder,"other.dll"),Plugin("other","1.0.0"));
 Check(Inventory.ProfileGroups(r,Profiles.Load(r),"offline").Count==0);
});
Test("profile mismatch errors name each missing and wrong-version DLL",()=>
{
 var(r,p)=Staged(("A-Wrong","wrong","1.0.0"));var policy=new Policy{Mods=new(){Mod("wrong","2.0.0"),Mod("missing","3.0.0")}};
 try{Inventory.CompleteProfileProposal(r,policy,p);throw new Exception("Mismatch accepted");}catch(InvalidDataException error){Check(error.Message.Contains("server requests 2.0.0, prepared 1.0.0")&&error.Message.Contains("Missing: missing 3.0.0"));}
});
Test("actual dependency DLLs pass selection review and partial filtering without bypassing server rules",()=>
{
 var(r,p)=Staged(("A-App","app","1.0.0"),("A-Library","detector","1.0.0"));var app=Mod("app");app.Package="A-App-1.0.0";var policy=new Policy{AllowExtras=true,Mods=new(){app}};var issues=new List<string>();var stage=SafePath.Under(r,"ModGate/staging/"+p.Id);
 var selection=ProfileSelection.WithDependencies(policy,p.Packages.Select(PackageId.Parse).ToArray(),policy.Mods,new HashSet<string>(),id=>Inventory.InspectProfilePayload(SafePath.Under(stage,"packages/"+id.Key)),issues.Add,new HashSet<string>{"A-Library-1.0.0"});
 Inventory.FilterPartialStage(r,policy,p,selection,issues);Verify(r,p);var mods=Inventory.CompleteProfileProposal(r,policy,p);ProfileSelection.ValidatePrepared(policy,mods,selection);
 Check(p.Packages.Count==2&&mods.Any(m=>m.Guid=="detector")&&issues.Single().Contains("Selected suggested dependency"));
});
Test("a dependency package containing an unchecked optional DLL is still removed in override",()=>
{
 var(r,p)=Staged(("A-App","app","1.0.0"),("A-Library","optional","1.0.0"));var app=Mod("app");app.Package="A-App-1.0.0";var policy=new Policy{AllowExtras=true,Mods=new(){app,Mod("optional",side:"optional")}};var issues=new List<string>();var stage=SafePath.Under(r,"ModGate/staging/"+p.Id);
 var selection=ProfileSelection.WithDependencies(policy,p.Packages.Select(PackageId.Parse).ToArray(),new[]{app},new HashSet<string>(),id=>Inventory.InspectProfilePayload(SafePath.Under(stage,"packages/"+id.Key)),issues.Add,new HashSet<string>{"A-Library-1.0.0"});
 Inventory.FilterPartialStage(r,policy,p,selection,issues);Verify(r,p);Check(p.Packages.Count==1&&issues.Single().Contains("unchecked"));
});
Test("actual TeleportEverything DLL exposes its soft modules without executing the mod",()=>
{
 var folder="A:/SteamLibrary/steamapps/common/Valheim/BepInEx/ModGate/staging/8862565eeefb4658aac0cdd1cb94a60a/packages/OdinPlus-TeleportEverything";
 if(!Directory.Exists(folder))return;
 var mod=Inventory.InspectProfilePayload(folder).Single();Check(mod.Guid=="odinplus.TeleportEverything"&&mod.Dependencies.Count==5&&mod.Dependencies.All(d=>d.Optional)&&mod.Dependencies.Any(d=>d.Guid=="odinplus.TeleportBoats"));
});
Test("production validation warns about hard DLL dependencies without blocking profile preparation",()=>
{
 var(r,p)=Staged(("A-App","app","1.0.0"));var file=SafePath.Under(r,"ModGate/staging/"+p.Id+"/packages/A-App/app.dll");
 using(var assembly=AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(file))))
 {
  var m=assembly.MainModule;var type=m.Types.Single(t=>t.Name=="Plugin");var dep=new TypeReference("BepInEx","BepInDependency",m,new AssemblyNameReference("BepInEx",new Version(5,4,23,0)));
  var ctor=new MethodReference(".ctor",m.TypeSystem.Void,dep){HasThis=true};ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
  var attr=new CustomAttribute(ctor);attr.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,"library"));attr.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,"2.0.0"));type.CustomAttributes.Add(attr);
  using var output=new MemoryStream();assembly.Write(output);File.WriteAllBytes(file,output.ToArray());
 }
 var policy=new Policy{AllowExtras=true,Mods=new(){Mod("app")}};
 var warnings=new List<string>();Check(Inventory.CompleteProfileProposal(r,policy,p,warn:warnings.Add).Any(m=>m.Guid=="app"));Check(warnings.Single().Contains("library >= 2.0.0")&&warnings[0].Contains("BepInEx may refuse"));
 var folder=SafePath.Under(r,"ModGate/staging/"+p.Id+"/packages/A-Library");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"library.dll"),Plugin("library","2.0.0"));
 warnings.Clear();Check(Inventory.CompleteProfileProposal(r,policy,p,warn:warnings.Add).Any(m=>m.Guid=="library")&&warnings.Count==0);
 File.WriteAllBytes(Path.Combine(folder,"library.dll"),Plugin("library","1.0.0"));warnings.Clear();Check(Inventory.CompleteProfileProposal(r,policy,p,warn:warnings.Add).Any(m=>m.Guid=="library")&&warnings.Single().Contains("selected version is 1.0.0"));
});

Test("clientOnly discovery reads installed DLL metadata without a loaded plugin instance",()=>
{
 var root=NewRoot();var plugins=Path.Combine(root,"plugins");var folder=Path.Combine(plugins,"A-Visual-2.0.0");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"visual.dll"),Plugin("visual","2.0.0"));
 var entry=Mod("visual",side:"clientOnly");entry.Automatic=true;var settings=new ServerManifest{Mods=new(){entry}};
 var detected=Inventory.ServerInstalledClientMods(settings,Array.Empty<ModEntry>(),plugins);
 Check(detected.Count==1&&detected[0].Guid=="visual"&&detected[0].Version=="2.0.0"&&detected[0].Package=="A-Visual-2.0.0");
 var next=AutoSetup.Reconcile(settings,detected,Array.Empty<string>());Check(next.Mods.Single().Side=="clientOnly"&&next.Mods[0].Version=="2.0.0");
 var policy=PolicyEngine.Publish(next,Array.Empty<ModEntry>());Check(policy.Mods.Any(m=>m.Guid=="visual"&&m.Version=="2.0.0"));
});
Test("production profile validation accepts a permitted newer DLL without changing server requirements",()=>
{
 var(r,p)=Staged(("A-Visual","visual","2.0.0"));var wanted=Mod("visual",side:"clientOnly");var policy=new Policy{AllowNewerClientVersions=true,Mods=new(){wanted}};
 var mods=Inventory.CompleteProfileProposal(r,policy,p);Check(mods.Single(m=>m.Guid=="visual").Version=="2.0.0"&&wanted.Version=="1.0.0");
 policy.AllowNewerClientVersions=false;try{Inventory.CompleteProfileProposal(r,policy,p);throw new Exception("accepted exact mismatch");}catch(InvalidDataException){}
});
Test("DLL soft integration metadata remains internal after server config migration",()=>
{
 var root=NewRoot();var folder=Path.Combine(root,"plugins","A-App-1.0.0");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"App.dll");
 using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(BepInEx.Paths).Assembly.Location));
 using(var assembly=AssemblyDefinition.ReadAssembly(new MemoryStream(Plugin("app","1.0.0")),new ReaderParameters{AssemblyResolver=resolver}))
 {
  var m=assembly.MainModule;var type=m.Types.Single(t=>t.Name=="Plugin");
  var ctor=m.ImportReference(typeof(BepInEx.BepInDependency).GetConstructor(new[]{typeof(string),typeof(BepInEx.BepInDependency.DependencyFlags)})!);var flags=ctor.Parameters[1].ParameterType;
  var attr=new CustomAttribute(ctor);attr.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,"kg.FindOutdatedStuff"));attr.ConstructorArguments.Add(new CustomAttributeArgument(flags,2));type.CustomAttributes.Add(attr);assembly.Write(file);
 }
 var mod=Inventory.InspectProfilePayload(folder).Single();Check(mod.Dependencies.Single().Optional);
 var settings=AutoSetup.Reconcile(new ServerManifest(),new[]{mod},Array.Empty<string>());var path=Path.Combine(root,"config","ModGate","server-mods.json");ServerConfig.Save(path,root,settings);
 var published=PolicyEngine.Publish(ServerConfig.Load(path,root),new[]{mod});Check(!File.ReadAllText(path).Contains("kg.FindOutdatedStuff")&&published.Mods.Single(m=>m.Guid=="app").Dependencies.Single().Guid=="kg.FindOutdatedStuff"&&published.Mods.All(m=>m.Guid!="kg.FindOutdatedStuff"));
});
Test("loose clientOnly DLLs use the exact server manifest without changing their DLL requirement",()=>
{
 var root=NewRoot();var plugins=Path.Combine(root,"plugins");var config=Path.Combine(root,"config");Directory.CreateDirectory(plugins);File.WriteAllBytes(Path.Combine(plugins,"Visual.dll"),Plugin("author.visual","2.7.8"));
 var entry=Mod("author.visual",version:"2.7.8",side:"clientOnly");entry.Automatic=true;var settings=new ServerManifest{Mods=new(){entry}};
 var detected=Inventory.ServerInstalledClientMods(settings,Array.Empty<ModEntry>(),plugins);var folder=LocalManifestReader.ServerManifestFolder(config,entry.Guid,detected);Directory.CreateDirectory(folder);
 File.WriteAllText(Path.Combine(folder,"manifest.json"),"{\"name\":\"VisualPackage\",\"version_number\":\"8.9.0\",\"website_url\":\"https://thunderstore.io/c/valheim/p/Author/VisualPackage/\",\"dependencies\":[]}");
 LocalManifestReader.ApplyServerManifests(detected,plugins,config,_=>{});ManifestCandidates.Enrich(detected);
 var effective=AutoSetup.Reconcile(settings,detected,Array.Empty<string>());ServerConfig.Save(Path.Combine(config,"ModGate/server-mods.json"),root,effective);
 var policy=PolicyEngine.Publish(effective,Array.Empty<ModEntry>());var mod=policy.Mods.Single(m=>m.Guid=="author.visual");Check(mod.Version=="2.7.8"&&mod.Side=="clientOnly"&&mod.Package=="Author-VisualPackage-8.9.0"&&File.Exists(Path.Combine(folder,"README.txt")));
});
Console.WriteLine("All "+passed+" production inventory checks passed.");
internal sealed class NeverSource:IPackageSource
{
 public Task<PackageMetadata> Metadata(PackageId id,CancellationToken token)=>throw new Exception("Unexpected metadata");
 public Task Download(PackageId id,string path,CancellationToken token)=>throw new Exception("Unexpected download");
}
