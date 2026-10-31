using System.Reflection;
using ModGate;
using UnityEngine;

internal static class CreatorSupportUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    private static void Call(Prompt prompt, string name) => typeof(Prompt).GetMethod(name, Private)!.Invoke(prompt, null);
    private static void Finish(Prompt prompt)
    {
        var task = Get(prompt, "featureTask") as Task;
        if (task != null && !SpinWait.SpinUntil(() => task.IsCompleted, 5000)) throw new Exception("Creator page fixture timed out.");
        Call(prompt, "UpdateFeatures"); GUI.Click = null; prompt.Draw();
    }
    internal static Prompt Fixture()
    {
        var root = BepInEx.Paths.BepInExRootPath;
        var state = new ProfileCatalog { Active = "offline", Servers = new() { new SavedProfile { Slot = "server-1", Name = "Test server", ServerKey = new string('a',64) } } };
        var mods = new List<ModEntry>();
        void Add(string relative, string guid, string name, string package)
        {
            var file = SafePath.Under(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file,"fixture metadata only");
            mods.Add(new ModEntry { Guid=guid,Name=name,Version="1.0.0",Package=package,LocalFile=file });
        }
        Add("plugins/Bascinet-OctoPack-1.0.0/pack.dll","pack","OctoPack","Bascinet-OctoPack-1.0.0");
        Add("plugins/Other-Apple-1.0.0/apple.dll","apple","Apple","Other-Apple-1.0.0");
        Add("plugins/Unknown/unknown.dll","unknown","Unknown mod","");
        Add("ModGate/profiles/server-1/plugins/Bascinet-OctoPack-2.0.0/pack.dll","pack","OctoPack","Bascinet-OctoPack-2.0.0");
        Add("ModGate/profiles/server-1/plugins/Other-Banana-1.0.0/banana.dll","banana","Banana","Other-Banana-1.0.0");
        Json.Save(Path.Combine(root,"ModGate/profiles.json"),state);
        Inventory.SupportInspect = path => mods.Where(m => File.Exists(m.LocalFile) && m.LocalFile.StartsWith(path.TrimEnd('\\','/') + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)).ToList();
        var prompt = new Prompt(_=>{},()=>{},()=>{}); prompt.ShowError("fixture"); Call(prompt,"OpenCreators"); Finish(prompt); return prompt;
    }
    internal static void Run(Action<string,Action> test,Action<bool> check)
    {
        test("creator sidebar page defaults to active profile and pins ModGate ahead of Bascinet and other installed mods",()=>
        {
            using var prompt=Fixture(); var state=(CreatorSupportResult)Get(prompt,"creators")!;
            check(!(bool)Get(prompt,"creatorsAllProfiles")! && state.Mods.Select(m=>m.Name).SequenceEqual(new[]{"ModGate","OctoPack","Apple","Unknown mod"}));
            check(GUI.Buttons.Any(b=>b.Text=="Support Mod Creators") && GUI.Buttons.All(b=>b.Text!="Review profile changes"));
            check(GUI.Buttons.Count(b=>b.Text.StartsWith("Support: "))>=4);
        });
        test("all-profile filter merges versions and changing back restores the active-only list",()=>
        {
            using var prompt=Fixture(); GUI.Click="All profiles";prompt.Draw();Finish(prompt);
            var all=(CreatorSupportResult)Get(prompt,"creators")!;check(all.Mods.Count==5 && all.Mods.Single(m=>m.Name=="OctoPack").Profiles.Count==2);
            GUI.Click="Active profile only";prompt.Draw();Finish(prompt);check(((CreatorSupportResult)Get(prompt,"creators")!).Mods.All(m=>m.Name!="Banana"));
        });
        test("outline star opens links without voting and blocks background navigation or click-through",()=>
        {
            using var prompt=Fixture();Application.LastUrl="";GUI.Click="Support: bascinet:4412";prompt.Draw();
            check(Get(prompt,"creatorPopup") is CreatorMod && Application.LastUrl=="");
            GUI.Click="Profiles";prompt.Draw();check((string)Get(prompt,"featurePage")! == "Creators" && Get(prompt,"creatorPopup")!=null);
            check(GUI.Buttons.Last(b=>b.Text=="Profiles").Enabled==false);
            GUI.Click="Nexus Mods ↗";prompt.Draw();check(Application.LastUrl=="https://www.nexusmods.com/valheim/mods/4412" && Get(prompt,"creatorPopup")!=null);
            GUI.Click="GitHub ↗";prompt.Draw();check(Application.LastUrl=="https://github.com/octerockman/OctoPack");
            GUI.Click="GitHub ↗";prompt.Draw();check(Application.LastUrl=="https://github.com/octerockman/OctoPack");
            check(GUILayout.Toggles.All(t=>!t.Text.Contains("endors")));
        });
        test("popup closes by close button and Escape without closing ModGate or leaving a vote state",()=>
        {
            using var prompt=Fixture();GUI.Click="Support: bascinet:4412";prompt.Draw();GUI.Click="×";prompt.Draw();check(Get(prompt,"creatorPopup")==null && prompt.IsVisible);
            GUI.Click="Support: bascinet:4412";prompt.Draw();Call(prompt,"HandleEscape");check(Get(prompt,"creatorPopup")==null && prompt.IsVisible);
            GUI.Click="Support: bascinet:4412";prompt.Draw();check(GUI.Buttons.Where(b=>b.Text.StartsWith("Support:")).All(b=>!b.Text.Contains("Liked")));
        });
        test("unknown pages and icon decode failures keep the creator list and popup usable",()=>
        {
            using var prompt=Fixture();GUI.Click="Support: guid:unknown";prompt.Draw();check(((CreatorMod)Get(prompt,"creatorPopup")!).Links.Count==0);
            check(DrawCapture.Commands.Any(c=>c.Text.Contains("No known mod-page links")));Call(prompt,"HandleEscape");
            var mod=((CreatorSupportResult)Get(prompt,"creators")!).Mods.Single(m=>m.Name=="OctoPack");
            var path=Path.Combine(BepInEx.Paths.BepInExRootPath,"plugins/Bascinet-OctoPack-1.0.0/icon.png");
            var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZlS8AAAAASUVORK5CYII=");File.WriteAllBytes(path,bytes);mod.Icons.Add(path);
            Call(prompt,"ClearCreatorIcons");ImageConversion.Reject=true;GUI.Click=null;prompt.Draw();check(((Dictionary<string,Texture2D?>)Get(prompt,"creatorIcons")!)[mod.Id]==null);
            ImageConversion.Reject=false;Call(prompt,"ClearCreatorIcons");prompt.Draw();check(((Dictionary<string,Texture2D?>)Get(prompt,"creatorIcons")!)[mod.Id]?.width==64 && File.ReadAllBytes(path).SequenceEqual(bytes));
        });
        test("creator page stays available with only ModGate and search never hides the pinned connector",()=>
        {
            using var prompt=Fixture();Inventory.SupportInspect=_=>new();Call(prompt,"RefreshCreators");Finish(prompt);
            typeof(Prompt).GetField("creatorsSearch",Private)!.SetValue(prompt,"unmatched");prompt.Draw();
            check(((CreatorSupportResult)Get(prompt,"creators")!).Mods.Count==1 && GUI.Buttons.Any(b=>b.Text=="Support: "+Constants.Guid));
        });
        foreach(var viewport in new[]{(1280,1000),(960,768),(640,900),(640,360)})
        test("creator popup and site buttons remain within viewport "+viewport,()=>
        {
            Screen.width=viewport.Item1;Screen.height=viewport.Item2;using var prompt=Fixture();GUI.Click="Support: bascinet:4412";prompt.Draw();GUI.Click=null;GUI.Buttons.Clear();prompt.Draw();
            foreach(var button in GUI.Buttons.Where(b=>b.Text.EndsWith(" ↗")&& b.Enabled || b.Text=="×"))check(button.Area.x>=0 && button.Area.y>=0 && button.Area.xMax<=Screen.width && button.Area.yMax<=Screen.height);
        });
    }
}
