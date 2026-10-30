using System.Reflection;
using ModGate;
using UnityEngine;

try
{
if (args.Contains("--visual-preview")) { VisualPreview.Export(args); return; }
var passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Test(string name, Action body)
{
    GUI.Buttons.Clear(); GUILayout.Toggles.Clear(); GUILayout.Labels.Clear(); GUI.Click = null; GUILayout.ThrowContent = false;
    DrawCapture.Clear(); DrawCapture.Enabled = true;
    BepInEx.Paths.BepInExRootPath = Path.Combine(Path.GetTempPath(), "ModGate-ui-case-" + Guid.NewGuid().ToString("N"));
    GUILayout.ScrollDepth = 0; GUILayout.AreaDepth = 0;
    GUI.enabled = true; Application.isBatchMode = false; ZInput.instance = new object(); Inventory.Current.Clear(); Inventory.Groups.Clear();
    Event.current = new Event { type = EventType.Repaint }; ZInput.Pressed.Clear(); ZInput.Released.Clear(); ZInput.Held.Clear(); ZInput.BeforeRead = null; ZInput.Wheel = 0;
    UnityEngine.EventSystems.EventSystem.current = null;
    body(); passed++; Console.WriteLine("PASS " + name);
}
void ExpandOtherInstalled(Prompt prompt)
{
    var label = GUI.Buttons.Where(b => b.Text.StartsWith("▶ Other installed mods", StringComparison.Ordinal)).Select(b => b.Text).LastOrDefault();
    if (label == null) return;
    GUI.Click = label; GUILayout.Toggles.Clear(); prompt.Draw();
}
if (args.Contains("--040-only"))
{
    Feature040Checks.Run(Test,Check);
    Console.WriteLine("All " + passed + " new profile feature simulations passed.");
    return;
}
if (args.Contains("--041-only"))
{
    Visual041Checks.Run(Test, Check);
    Console.WriteLine("All " + passed + " production layout checks passed.");
    return;
}
if (args.Contains("--042-only"))
{
    Feature042Checks.Run(Test, Check);
    Console.WriteLine("All " + passed + " shortcut and config accordion checks passed.");
    return;
}
if (args.Contains("--admin-only"))
{
    AdminConfigUiChecks.Run(Test,Check);
    Console.WriteLine("All " + passed + " admin config simulations passed.");
    return;
}
if (args.Contains("--health-ui-only"))
{
    ServerHealthUiChecks.Run(Test, Check);
    Console.WriteLine("All " + passed + " server health UI checks passed.");
    return;
}
Test("error immediately unlocks and shows the mouse", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { });
    Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
    prompt.ShowError("Classify ServersideQoL");
    Check(prompt.IsVisible && Cursor.lockState == CursorLockMode.None && Cursor.visible);
});
Test("late cursor enforcement releases a lock reapplied by the game", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error");
    Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; prompt.EnsureCursor();
    Check(Cursor.lockState == CursorLockMode.None && Cursor.visible);
});
Test("native keyboard Escape dismisses without requiring mouse or drawing", () =>
{
    var closed = 0; using var prompt = new Prompt(_ => { }, () => closed++, () => { }); prompt.ShowError("error");
    GUIUtility.hotControl = 99; GUIUtility.keyboardControl = 25; ZInput.Pressed.Add(KeyCode.Escape); prompt.Update();
    Check(!prompt.IsVisible && closed == 1 && Cursor.lockState == CursorLockMode.None && Cursor.visible);
    Check(GUIUtility.hotControl == 0 && GUIUtility.keyboardControl == 0);
});
Test("GUI Escape is a fallback when normal Update has not run", () =>
{
    var closed = 0; using var prompt = new Prompt(_ => { }, () => closed++, () => { }); prompt.ShowError("error");
    Event.current = new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }; prompt.Draw();
    Check(!prompt.IsVisible && closed == 1 && Event.current.type == EventType.Used);
});
Test("Back to menu button closes the error", () =>
{
    var closed = 0; using var prompt = new Prompt(_ => { }, () => closed++, () => { }); prompt.ShowError("error");
    GUI.Click = "Back to menu (Esc)"; prompt.Draw(); Check(closed == 1 && !prompt.IsVisible);
});
Test("Quit button requests the normal game exit", () =>
{
    var quit = 0; using var prompt = new Prompt(_ => { }, () => { }, () => quit++); prompt.ShowError("error");
    GUI.Click = "Quit Valheim"; prompt.Draw(); Check(quit == 1 && Cursor.lockState == CursorLockMode.None);
});
foreach (var size in new[] { (640, 360), (1024, 768), (1920, 1080), (3840, 2160) })
    Test("exit buttons remain visible outside long-content scroll at " + size, () =>
    {
        Screen.width = size.Item1; Screen.height = size.Item2;
        using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError(new string('x', 50000)); prompt.Draw();
        foreach (var label in new[] { "Back to menu (Esc)", "Quit Valheim" })
        {
            var record = GUI.Buttons.Single(b => b.Text == label);
            Check(record.Enabled && !record.InScroll);
            Check(record.Area.x >= 0 && record.Area.y >= 0 && record.Area.x + record.Area.width <= Screen.width && record.Area.y + record.Area.height <= Screen.height);
        }
        Check(GUILayout.AreaDepth == 0 && GUILayout.ScrollDepth == 0);
    });
Test("an inherited disabled GUI cannot disable exit controls and its state is restored", () =>
{
    var oldSkin = GUI.skin; var oldMatrix = new Matrix4x4(7); var oldColor = new Color(0, 0, 0, 0);
    GUI.matrix = oldMatrix; GUI.color = oldColor; GUI.backgroundColor = oldColor; GUI.contentColor = oldColor; GUI.enabled = false; GUI.depth = 77;
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error"); prompt.Draw();
    Check(GUI.Buttons.All(b => b.Enabled));
    Check(ReferenceEquals(oldSkin, GUI.skin) && GUI.matrix == oldMatrix && GUI.color == oldColor && GUI.backgroundColor == oldColor && GUI.contentColor == oldColor && !GUI.enabled && GUI.depth == 77);
});
Test("content failure leaves fixed exit controls available and Escape still works", () =>
{
    var closed = 0; using var prompt = new Prompt(_ => { }, () => closed++, () => { }); prompt.ShowError("error");
    GUILayout.ThrowContent = true; prompt.Draw();
    Check(GUI.Buttons.Any(b => b.Text == "Back to menu (Esc)") && GUI.Buttons.Any(b => b.Text == "Quit Valheim"));
    Check(GUILayout.AreaDepth == 0 && GUILayout.ScrollDepth == 0);
    ZInput.Pressed.Add(KeyCode.Escape); prompt.Update(); Check(closed == 1 && !prompt.IsVisible);
});
Test("Escape cancels a running download and closes immediately", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error");
    using var cancellation = new CancellationTokenSource();
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var taskField = typeof(Prompt).GetField("task", flags)!;
    var resultType = taskField.FieldType.GenericTypeArguments[0];
    var source = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType))!;
    taskField.SetValue(prompt, source.GetType().GetProperty("Task")!.GetValue(source));
    typeof(Prompt).GetField("cancellation", flags)!.SetValue(prompt, cancellation);
    ZInput.Pressed.Add(KeyCode.Escape); prompt.Update();
    Check(cancellation.IsCancellationRequested && !prompt.IsVisible);
    source.GetType().GetMethod("SetCanceled", Type.EmptyTypes)!.Invoke(source, null);
    prompt.Update();
});
Test("Shift+F6 uses the game's keyboard input to open the manager", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); ZInput.ManagerShortcut(); prompt.Update();
    Check(prompt.IsVisible && Cursor.visible && Cursor.lockState == CursorLockMode.None);
});
Test("closing a scheduled change does not remove the pending request", () =>
{
    Profiles.Schedule(BepInEx.Paths.BepInExRootPath, new ProfileCommand { Kind = "removeAll" });
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error");
    ZInput.Pressed.Add(KeyCode.Escape); prompt.Update(); Check(Profiles.Pending(BepInEx.Paths.BepInExRootPath));
});
Test("cursor is available before the game's input system is initialized", () =>
{
    ZInput.instance = null; using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error");
    Check(Cursor.visible && Cursor.lockState == CursorLockMode.None);
});
Test("disposal stops cursor ownership without restoring an old locked state", () =>
{
    var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error"); prompt.Dispose();
    Check(!prompt.IsVisible && Cursor.visible && Cursor.lockState == CursorLockMode.None);
});
foreach (var action in new[] { "Back to menu (Esc)", "Quit Valheim" })
    Test("Valheim mouse input activates " + action + " without legacy GUI mouse events", () =>
    {
        var closed = 0; var quit = 0;
        using var prompt = new Prompt(_ => { }, () => closed++, () => quit++); prompt.ShowError("error"); prompt.Draw();
        var area = GUI.Buttons.Single(b => b.Text == action).Area;
        ZInput.pointerPosition = new Vector3(area.x + area.width / 2, Screen.height - area.y - area.height / 2, 0);
        ZInput.Pressed.Add(KeyCode.Mouse0); prompt.Update();
        ZInput.Released.Add(KeyCode.Mouse0); prompt.Update(); prompt.Draw();
        Check(action.StartsWith("Back") ? closed == 1 && quit == 0 : quit == 1 && closed == 0);
    });
Test("dragging into an exit button does not activate it", () =>
{
    var closed = 0; using var prompt = new Prompt(_ => { }, () => closed++, () => { }); prompt.ShowError("error"); prompt.Draw();
    var area = GUI.Buttons.Single(b => b.Text == "Back to menu (Esc)").Area;
    ZInput.pointerPosition = new Vector3(0, 0, 0); ZInput.Pressed.Add(KeyCode.Mouse0); prompt.Update();
    ZInput.pointerPosition = new Vector3(area.x + 1, Screen.height - area.y - 1, 0); ZInput.Released.Add(KeyCode.Mouse0); prompt.Update(); prompt.Draw();
    Check(closed == 0 && prompt.IsVisible);
});
Test("the background menu is blocked only while the prompt is open", () =>
{
    var menu = new UnityEngine.EventSystems.EventSystem(); UnityEngine.EventSystems.EventSystem.current = menu;
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error"); Check(menu.enabled && !menu.currentInputModule.enabled);
    ZInput.Pressed.Add(KeyCode.Escape); prompt.Update(); Check(menu.enabled && menu.currentInputModule.enabled);
});
Test("a menu created during scene return is also restored when closing", () =>
{
    var old = new UnityEngine.EventSystems.EventSystem(); UnityEngine.EventSystems.EventSystem.current = old;
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("error");
    var next = new UnityEngine.EventSystems.EventSystem(); UnityEngine.EventSystems.EventSystem.current = next; prompt.EnsureCursor();
    Check(old.enabled && next.enabled && !old.currentInputModule.enabled && !next.currentInputModule.enabled);
    prompt.Dispose(); Check(old.currentInputModule.enabled && next.currentInputModule.enabled);
});
Test("scroll wheel input reaches long messages without legacy GUI events", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError(new string('x', 50000)); prompt.Draw();
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var bounds = (Rect)typeof(Prompt).GetField("scrollBounds", flags)!.GetValue(prompt)!;
    ZInput.pointerPosition = new Vector3(bounds.x + 5, Screen.height - bounds.y - 5, 0); ZInput.Wheel = -1; prompt.Update();
    var scroll = (Vector2)typeof(Prompt).GetField("scroll", flags)!.GetValue(prompt)!;
    Check(scroll.y > 0);
});
Test("Nexus page button opens only the validated owner URL", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { });
    var mod = new ModEntry { Guid = "manual", Name = "Nexus Mod", Version = "1.0.0", Side = "required", NexusUrl = "https://www.nexusmods.com/valheim/mods/123?tab=files" };
    var policy = new Policy { ServerName = "Test", Mods = new List<ModEntry> { mod } };
    prompt.Show(policy, new Comparison { Missing = { mod } }, Array.Empty<ModEntry>(), new string('a', 64));
    Application.LastUrl = ""; GUI.Click = "Open Nexus download page: Nexus Mod"; prompt.Draw();
    Check(Application.LastUrl == mod.NexusUrl);
});
Test("optional installed mods start unchecked and can be selected", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { });
    var optional = new ModEntry { Guid = "optional", Name = "Camping", Version = "1.0.0", Side = "optional" };
    var installed = new ModEntry { Guid = "optional", Name = "Camping", Version = "2.0.0" };
    var policy = new Policy { ServerName = "Test", Mods = new List<ModEntry> { optional } };
    prompt.Show(policy, PolicyEngine.Compare(policy, new[] { installed }), new[] { installed }, new string('c',64)); prompt.Draw();
    var row = GUILayout.Toggles.Single(t => t.Text.TrimStart().StartsWith("Optional: Camping")); Check(!row.Selected && row.Enabled);
    GUI.Click = row.Text; prompt.Draw();
    var choices = (HashSet<string>)typeof(Prompt).GetField("selectedMods",BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prompt)!;
    Check(choices.Contains("optional"));
    GUI.Click = row.Text; prompt.Draw(); Check(!choices.Contains("optional"));
});
Test("required checkboxes are locked and disallowed local extras are disabled", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { });
    var required = new ModEntry { Guid = "required", Name = "Required", Version = "1.0.0", Side = "required" };
    var extra = new ModEntry { Guid = "extra", Name = "Extra", Version = "1.0.0" };
    var policy = new Policy { ServerName = "Test", Mods = new List<ModEntry> { required } };
    prompt.Show(policy, PolicyEngine.Compare(policy,new[]{extra}),new[]{extra},new string('d',64)); prompt.Draw();
    ExpandOtherInstalled(prompt);
    Check(GUILayout.Toggles.Single(t=>t.Text.StartsWith("Required:")).Selected && !GUILayout.Toggles.Single(t=>t.Text.StartsWith("Required:")).Enabled);
    Check(!GUILayout.Toggles.Single(t=>t.Text.StartsWith("Keep installed:")).Enabled);
});
Test("permitted installed extras have working inclusion checkboxes", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { });
    var extra = new ModEntry { Guid = "extra", Name = "Extra", Version = "1.0.0" };
    var policy = new Policy { ServerName = "Test", AllowExtras = true };
    prompt.Show(policy,new Comparison(),new[]{extra},new string('e',64)); prompt.Draw();
    ExpandOtherInstalled(prompt);
    var row = GUILayout.Toggles.Single(t=>t.Text.StartsWith("Keep installed:")); Check(row.Enabled && !row.Selected);
    GUI.Click = row.Text; prompt.Draw();
    var choices = (HashSet<string>)typeof(Prompt).GetField("selectedMods",BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prompt)!;
    Check(choices.Contains("extra"));
});
Test("mod checkbox text uses the requested status colors", () =>
{
    using var prompt = new Prompt(_ => { }, () => { }, () => { });
    var good = new ModEntry { Guid="good",Name="Good",Version="1.0.0",Side="required" };
    var missing = new ModEntry { Guid="missing",Name="Missing",Version="1.0.0",Side="required" };
    var wrong = new ModEntry { Guid="wrong",Name="Wrong",Version="1.0.0",Side="required" };
    var optional = new ModEntry { Guid="optional",Name="Optional",Version="1.0.0",Side="optional" };
    var local = new[]{new ModEntry{Guid="good",Name="Good",Version="1.0.0.0"},new ModEntry{Guid="wrong",Name="Wrong",Version="2.0.0"}};
    var policy = new Policy { ServerName="Test",Mods=new List<ModEntry>{good,missing,wrong,optional} };
    prompt.Show(policy,PolicyEngine.Compare(policy,local),local,new string('f',64)); GUI.Click="All";prompt.Draw();
    Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Good").TextColor == new Color(.604f,.812f,.667f,1));
    Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Missing").TextColor == new Color(.945f,.631f,.596f,1));
    Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Wrong").TextColor == new Color(.914f,.780f,.490f,1));
    Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Optional").TextColor == new Color(.914f,.780f,.490f,1));
});
Test("window uses the bounded desktop size when screen space allows", () =>
{
    Screen.width=1920;Screen.height=1080;
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.ShowError("test");prompt.Draw();
    Check(GUI.LastWindow.width==1320 && GUI.LastWindow.height==960);
});
Test("restart now invokes the restart action only for scheduled changes", () =>
{
    var restarted=0;using var prompt=new Prompt(_=>{},()=>{},()=>{},()=>restarted++);
    prompt.ShowError("test");
    typeof(Prompt).GetField("scheduled",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(prompt,true);
    GUI.Click="Restart now";prompt.Draw();Check(restarted==1);
});
Test("install and restart schedules before invoking the restart callback", () =>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-restart-ui-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(BepInEx.Paths.BepInExRootPath);
    System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ValheimModGate.Preloader") { Version = new Version(Constants.Version + ".0") },System.Reflection.Emit.AssemblyBuilderAccess.Run);
    var mod = new ModEntry{Guid="a",Name="A",Version="1.0.0"};
    var pending=new PendingInstall{Id=Guid.NewGuid().ToString("N")};
    var profile=new SavedProfile{Slot="server-1",ServerKey=new string('9',64),Name="Test",Mods=new List<ModEntry>{mod}};
    var restarted=0;using var prompt=new Prompt(_=>{},()=>{},()=>{},()=>{Check(Profiles.Pending(BepInEx.Paths.BepInExRootPath));restarted++;});
    prompt.ShowError("test");
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
    typeof(Prompt).GetField("prepared",flags)!.SetValue(prompt,pending);typeof(Prompt).GetField("preparedProfile",flags)!.SetValue(prompt,profile);
    GUI.Click="Install and restart";prompt.Draw();
    Check(restarted==1 && Profiles.Pending(BepInEx.Paths.BepInExRootPath));
});
Test("restart failure preserves the scheduled change and keeps the prompt usable", () =>
{
    using var prompt=new Prompt(_=>{},()=>{},()=>{},()=>throw new IOException("helper unavailable"));prompt.ShowError("test");
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;typeof(Prompt).GetField("scheduled",flags)!.SetValue(prompt,true);
    GUI.Click="Restart now";prompt.Draw();
    Check((bool)typeof(Prompt).GetField("scheduled",flags)!.GetValue(prompt)!);
    Check(((string)typeof(Prompt).GetField("error",flags)!.GetValue(prompt)!).Contains("helper unavailable"));
    Check(prompt.IsVisible);
});
Test("profile config source checkboxes are mutually exclusive with active default", () =>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-config-ui-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});var policy=new Policy{ServerName="Test"};prompt.Show(policy,new Comparison(),Array.Empty<ModEntry>(),new string('8',64));GUI.Click="Configs";prompt.Draw();
    Check(GUILayout.Toggles.Single(t=>t.Text=="Copy current profile configs").Selected);
    GUI.Click="Start fresh with default configs";prompt.Draw();Check((string)typeof(Prompt).GetField("configSource",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(prompt)! =="fresh");
    GUI.Click="Copy offline profile configs";prompt.Draw();Check((string)typeof(Prompt).GetField("configSource",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(prompt)! =="offline");
});
Test("profile delete shows confirmation and cancel leaves files and request untouched", () =>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-delete-ui-"+Guid.NewGuid().ToString("N"));
    Json.Save(SafePath.Under(BepInEx.Paths.BepInExRootPath,"ModGate/profiles.json"),new ProfileCatalog{Servers=new List<SavedProfile>{new SavedProfile{Slot="server-1",ServerKey=new string('1',64),Name="One"}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();GUI.Click="More (server-1)";prompt.Draw();GUI.Click="Delete server-1";prompt.Draw();
    Check(!Profiles.Pending(BepInEx.Paths.BepInExRootPath));GUI.Buttons.Clear();prompt.Draw();Check(GUI.Buttons.Any(b=>b.Text=="Confirm deletion"));
    GUI.Click="Cancel deletion (Esc)";prompt.Draw();Check(!Profiles.Pending(BepInEx.Paths.BepInExRootPath)&&Profiles.Load(BepInEx.Paths.BepInExRootPath).Servers.Count==1&&prompt.IsVisible);
});
Test("confirmed profile deletion schedules only the chosen server profile", () =>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-delete-confirm-ui-"+Guid.NewGuid().ToString("N"));
    Json.Save(SafePath.Under(BepInEx.Paths.BepInExRootPath,"ModGate/profiles.json"),new ProfileCatalog{Servers=new List<SavedProfile>{new SavedProfile{Slot="server-1",ServerKey=new string('1',64),Name="One"}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();GUI.Click="More (server-1)";prompt.Draw();GUI.Click="Delete server-1";prompt.Draw();GUI.Click="Confirm deletion";prompt.Draw();
    var command=Json.Read<ProfileCommand>(File.ReadAllText(SafePath.Under(BepInEx.Paths.BepInExRootPath,"ModGate/profile-request.json")));Check(command.Kind=="delete"&&command.Target=="server-1");
});
Test("download review identifies the actual manifest fallback source",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-source-review-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});var mod=new ModEntry{Guid="a",Name="A",Version="1.0.0",Side="required"};var policy=new Policy{ServerName="Review",Mods=new List<ModEntry>{mod}};
    prompt.Show(policy,new Comparison{Missing={mod}},Array.Empty<ModEntry>(),new string('7',64));
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;typeof(Prompt).GetField("prepared",flags)!.SetValue(prompt,new PendingInstall{Id=Guid.NewGuid().ToString("N"),Packages=new List<string>{"github:Author/Shared/v1.0.0/Shared.zip"}});
    typeof(Prompt).GetField("preparedProfile",flags)!.SetValue(prompt,new SavedProfile{Name="Review"});prompt.Draw();
    Check(GUILayout.Labels.Any(s=>s.Contains("GitHub release; source is the installed mod manifest")));
});
Test("setup failure offers override outside the scroll area and keeps escape usable",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-override-ui-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{ServerName="Test"},new Comparison(),Array.Empty<ModEntry>(),new string('7',64));
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;var result=typeof(Prompt).GetNestedType("PreparedProfile",BindingFlags.NonPublic)!;
    var fault=typeof(Task).GetMethods().Single(m=>m.Name=="FromException"&&m.IsGenericMethod).MakeGenericMethod(result).Invoke(null,new object[]{new IOException("download unavailable")});
    typeof(Prompt).GetField("task",flags)!.SetValue(prompt,fault);prompt.Update();prompt.Draw();
    Check(GUI.Buttons.Any(b=>b.Text=="Override and continue anyway"&&b.Enabled&&!b.InScroll));
    Check(GUI.Buttons.Any(b=>b.Text=="Retry setup"));ZInput.Pressed.Add(KeyCode.Escape);prompt.Update();Check(!prompt.IsVisible);
});
Test("stopped setup offers retry and override only after cancellation completes",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-cancel-ui-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{ServerName="Test"},new Comparison(),Array.Empty<ModEntry>(),new string('7',64));
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;var result=typeof(Prompt).GetNestedType("PreparedProfile",BindingFlags.NonPublic)!;
    var cancelled=typeof(Task).GetMethods().Single(m=>m.Name=="FromCanceled"&&m.IsGenericMethod).MakeGenericMethod(result).Invoke(null,new object[]{new CancellationToken(true)});
    typeof(Prompt).GetField("task",flags)!.SetValue(prompt,cancelled);prompt.Update();prompt.Draw();Check(GUI.Buttons.Any(b=>b.Text=="Override and continue anyway"&&b.Enabled)&&GUI.Buttons.Any(b=>b.Text=="Retry setup"&&b.Enabled));
});
Test("override prepares and reviews missing mods before scheduling the partial profile",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-partial-ui-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});var mod=new ModEntry{Guid="missing",Name="Missing",Version="1.0.0",Side="required"};
    prompt.Show(new Policy{ServerName="Partial",Mods=new List<ModEntry>{mod}},new Comparison{Missing={mod}},Array.Empty<ModEntry>(),new string('7',64));
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;typeof(Prompt).GetField("overrideAvailable",flags)!.SetValue(prompt,true);
    GUI.Click="Override and continue anyway";prompt.Draw();
    var task=(Task?)typeof(Prompt).GetField("task",flags)!.GetValue(prompt);Check(task!=null);Check(SpinWait.SpinUntil(()=>task!.IsCompleted,5000));prompt.Update();
    Check(typeof(Prompt).GetField("prepared",flags)!.GetValue(prompt)!=null);Check(!Profiles.Pending(BepInEx.Paths.BepInExRootPath));
    GUILayout.Labels.Clear();prompt.Draw();Check(GUILayout.Labels.Any(l=>l.Contains("PARTIAL PROFILE"))&&GUILayout.Labels.Any(l=>l.Contains("Missing 1.0.0: missing")));
    GUI.Click="Install on next launch";prompt.Draw();var command=Json.Read<ProfileCommand>(File.ReadAllText(SafePath.Under(BepInEx.Paths.BepInExRootPath,"ModGate/profile-request.json")));
    Check(command.Profile!.Incomplete&&command.Install!.Packages.Count==0);
});
Test("a new server prompt clears prior override state",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-override-reset-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});var flags=BindingFlags.Instance|BindingFlags.NonPublic;
    typeof(Prompt).GetField("overrideAvailable",flags)!.SetValue(prompt,true);prompt.Show(new Policy{ServerName="New"},new Comparison(),Array.Empty<ModEntry>(),new string('7',64));prompt.Draw();
    Check(GUI.Buttons.All(b=>b.Text!="Override and continue anyway"));
});
Test("home profile manager offers view and edit buttons for offline and saved servers",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-edit-buttons-"+Guid.NewGuid().ToString("N"));
    Json.Save(SafePath.Under(BepInEx.Paths.BepInExRootPath,"ModGate/profiles.json"),new ProfileCatalog{Servers=new(){new SavedProfile{Slot="server-1",ServerKey=new string('1',64),Name="One"}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();prompt.Draw();
    Check(GUI.Buttons.Any(b=>b.Text=="Open profile (offline)")&&GUI.Buttons.Any(b=>b.Text=="Open profile (server-1)"));
});
Test("profile editor shows names versions disabled entries and bundled checkboxes",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-edit-list-"+Guid.NewGuid().ToString("N"));
    Inventory.Groups.Add(new ProfileEditGroup{PluginPath="Bundle",Directory=true,Enabled=false,Mods=new(){new ModEntry{Guid="a",Name="Alpha",Version="1.2.3"},new ModEntry{Guid="b",Name="Beta",Version="2.3.4"}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();GUI.Click="Open profile (offline)";prompt.Draw();GUI.Buttons.Clear();prompt.Draw();
    Check(GUILayout.Toggles.Any(t=>t.Text=="Alpha 1.2.3 (bundled)"&&!t.Selected)&&GUILayout.Toggles.Any(t=>t.Text=="Beta 2.3.4 (bundled)"));
    GUI.Click="Alpha 1.2.3 (bundled)";prompt.Draw();var groups=(List<ProfileEditGroup>)typeof(Prompt).GetField("editGroups",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(prompt)!;Check(groups.Single().Enabled);
    Check(GUI.Buttons.Any(b=>b.Text=="Back to profiles (Esc)"));
});
Test("escape leaves the editor without scheduling or closing the profile manager",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-edit-cancel-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();GUI.Click="Open profile (offline)";prompt.Draw();ZInput.Pressed.Add(KeyCode.Escape);prompt.Update();
    Check(prompt.IsVisible&&!Profiles.Pending(BepInEx.Paths.BepInExRootPath));Check(typeof(Prompt).GetField("editingSlot",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(prompt)==null);
});
Test("profile edit review precedes scheduling and leaves original files untouched",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-edit-review-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;
    var source=Path.Combine(root,"plugins","Example","mod.dll");Directory.CreateDirectory(Path.GetDirectoryName(source)!);File.WriteAllText(source,"original");
    var mod=new ModEntry{Guid="example",Name="Example",Version="1.0.0",LocalFile=source};Inventory.Current.Add(mod);Inventory.Groups.Add(new ProfileEditGroup{PluginPath="Example",Directory=true,Enabled=true,WasEnabled=true,Mods=new(){mod}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();GUI.Click="Open profile (offline)";prompt.Draw();GUI.Click="Example 1.0.0";prompt.Draw();GUI.Click="Review profile changes";prompt.Draw();
    var task=(Task?)typeof(Prompt).GetField("task",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(prompt);Check(task!=null&&SpinWait.SpinUntil(()=>task.IsCompleted,5000));prompt.Update();
    Check(!Profiles.Pending(root)&&File.ReadAllText(source)=="original");prompt.Draw();Check(GUILayout.Labels.Any(l=>l.Contains("Disable: Example")));
    GUI.Click="Save on next launch";prompt.Draw();var command=Json.ReadLocal<ProfileCommand>(SafePath.Under(root,"ModGate/profile-request.json"));Check(command.Kind=="edit"&&command.Target=="offline"&&!command.Edit!.Groups.Single().Enabled&&File.Exists(source));
});
Test("setup log button remains outside the scroll and opens the generated local log",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-log-button-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.ShowError("Dependency A-Library is too old");GUI.Click="Open setup log";prompt.Draw();
    Check(GUI.Buttons.Any(b=>b.Text=="Open setup log"&&b.Enabled&&!b.InScroll));var path=SafePath.Under(BepInEx.Paths.BepInExRootPath,"ModGate/logs/profile-setup.txt");Check(Application.LastUrl==new Uri(path).AbsoluteUri&&File.ReadAllText(path).Contains("A-Library"));
});
Test("rejoining a saved server preserves its optional and permitted extra selections",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-saved-selections-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;var key=new string('4',64);
    var optional=new ModEntry{Guid="optional",Name="Optional",Version="1.0.0",Side="optional"};var extra=new ModEntry{Guid="extra",Name="Extra",Version="1.0.0"};
    Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Servers=new(){new SavedProfile{Slot="server-1",ServerKey=key,Name="Saved",Mods=new(){optional,extra}}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{AllowExtras=true,Mods=new(){optional}},new Comparison(),new[]{optional,extra},key);GUI.Click="All";prompt.Draw();
    ExpandOtherInstalled(prompt);
    Check(GUILayout.Toggles.Any(t=>t.Text.TrimStart().StartsWith("Optional: Optional")&&t.Selected)&&GUILayout.Toggles.Any(t=>t.Text.StartsWith("Keep installed: Extra")&&t.Selected));
});
Test("active partial profile status and exact differences are recorded for failed joins",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-active-diagnostics-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;var wanted=new ModEntry{Guid="wrong",Name="Wrong",Version="2.0.0",Side="required"};
    Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Active="server-1",Servers=new(){new SavedProfile{Slot="server-1",ServerKey=new string('5',64),Name="Saved",Incomplete=true}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){wanted}},new Comparison{WrongVersion={wanted}},new[]{new ModEntry{Guid="wrong",Name="Wrong",Version="1.0.0"}},new string('5',64));prompt.Draw();
    Check(GUILayout.Labels.Any(l=>l.Contains("This profile is active but was saved as partial")));var text=File.ReadAllText(SafePath.Under(root,"ModGate/logs/profile-setup.txt"));Check(text.Contains("Active profile: server-1")&&text.Contains("server requires 2.0.0; loaded 1.0.0"));
});
Test("connector version mismatch explains both DLL updates and disables pointless preparation",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-connector-mismatch-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){new ModEntry{Guid=Constants.Guid,Name="ModGate",Version="0.0.1",Side="required"}}},new Comparison(),Array.Empty<ModEntry>(),new string('7',64));prompt.Draw();
    Check(GUI.Buttons.Any(b=>b.Text=="Update both ModGate DLLs on server and client first"&&!b.Enabled));Check(GUI.Buttons.All(b=>b.Text!="Download and review"));
    Check(GUILayout.Labels.Any(l=>l.Contains("server requires ModGate 0.0.1")));
});
Test("a saved profile containing automatic dependencies is reusable on reconnect",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-dependency-cache-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;var key=new string('8',64);
    var app=new ModEntry{Guid="app",Name="App",Version="1.0.0",Side="required"};var dependency=new ModEntry{Guid="dependency",Name="Dependency",Version="1.0.0"};
    Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Servers=new(){new SavedProfile{Slot="server-1",ServerKey=key,Name="Saved",Mods=new(){app,dependency}}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{AllowExtras=true,Mods=new(){app}},new Comparison{Missing={app}},new[]{dependency},key);prompt.Draw();
    ExpandOtherInstalled(prompt);
    Check(GUI.Buttons.Any(b=>b.Text=="Use saved server profile"&&b.Enabled));Check(GUILayout.Toggles.Any(t=>t.Text.StartsWith("Keep installed: Dependency")&&t.Selected));
});
Test("a newly required mod prompts preparation instead of reusing a stale saved profile",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-new-server-mod-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;var key=new string('9',64);
    var app=new ModEntry{Guid="app",Name="App",Version="1.0.0",Side="required"};var added=new ModEntry{Guid="new-required",Name="New Required Mod",Version="1.0.0",Side="required"};
    Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Active="server-1",Servers=new(){new SavedProfile{Slot="server-1",ServerKey=key,Name="Saved",Mods=new(){app}}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{AllowExtras=true,Mods=new(){app,added}},new Comparison{Missing={added}},new[]{app},key);prompt.Draw();
    Check(prompt.IsVisible&&GUI.Buttons.Any(b=>b.Text=="Download and review"&&b.Enabled)&&GUI.Buttons.All(b=>b.Text!="Use saved server profile"));
    Check(File.ReadAllText(SafePath.Under(root,"ModGate/logs/profile-setup.txt")).Contains("new-required"));
});
Test("profile cards show all five slots and settings stays outside the scroll",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-cards030-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();prompt.Draw();
    Check(Enumerable.Range(1,4).All(i=>GUILayout.Labels.Contains("Server slot "+i))&&GUI.Buttons.Any(b=>b.Text=="Open profile (offline)"));
    Check(GUI.Buttons.Any(b=>b.Text=="Settings"&&!b.InScroll));
});
Test("tabs remain fixed and navigating configs back preserves module choices",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-tabs030-"+Guid.NewGuid().ToString("N"));
    var optional=new ModEntry{Guid="choice",Name="Choice",Version="1.0.0",Side="optional"};
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){optional}},new Comparison(),Array.Empty<ModEntry>(),new string('a',64));prompt.Draw();
    var row=GUILayout.Toggles.Single(t=>t.Text.TrimStart().StartsWith("Optional: Choice"));GUI.Click=row.Text;prompt.Draw();GUI.Click="Configs";prompt.Draw();
    Check(GUI.Buttons.Any(b=>b.Text=="Activity"&&!b.InScroll));GUI.Click="Back to menu (Esc)";prompt.Draw();
    var choices=(HashSet<string>)typeof(Prompt).GetField("selectedMods",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(prompt)!;
    Check(prompt.IsVisible&&choices.Contains("choice"));
});
Test("optional module groups start expanded and remain collapsible and selectable",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-modules030-"+Guid.NewGuid().ToString("N"));
    var parent=new ModEntry{Guid="teleport",Name="TeleportEverything",Version="3.0.11",Side="required",Dependencies=new(){new PluginDependency{Guid="boats",Optional=true}}};
    var child=new ModEntry{Guid="boats",Name="TeleportBoats",Version="0.1.3",Side="optional"};
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){parent,child}},new Comparison(),Array.Empty<ModEntry>(),new string('b',64));prompt.Draw();
    Check(GUILayout.Toggles.Any(t=>t.Text.Contains("Optional: TeleportBoats")));
    GUI.Click=GUI.Buttons.Last(b=>b.Text.StartsWith("▼ Optional mods",StringComparison.Ordinal)).Text;GUILayout.Toggles.Clear();prompt.Draw();Check(GUILayout.Toggles.All(t=>!t.Text.Contains("TeleportBoats")));
    GUI.Buttons.Clear();prompt.Draw();
    GUI.Click=GUI.Buttons.Last(b=>b.Text.StartsWith("▶ Optional mods",StringComparison.Ordinal)).Text;GUILayout.Toggles.Clear();prompt.Draw();
    var row=GUILayout.Toggles.Last(t=>t.Text.Contains("Optional: TeleportBoats"));Check(row.Enabled&&!row.Selected);GUI.Click=row.Text;prompt.Draw();
    Check(((HashSet<string>)typeof(Prompt).GetField("selectedMods",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(prompt)!).Contains("boats"));
});
Test("hard-declared optional modules remain optional unchecked and selectable",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-hard030-"+Guid.NewGuid().ToString("N"));
    var parent=new ModEntry{Guid="parent",Name="Parent",Version="1.0.0",Side="required",Dependencies=new(){new PluginDependency{Guid="child",Optional=false}}};
    var child=new ModEntry{Guid="child",Name="Child",Version="1.0.0",Side="optional"};
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){parent,child}},new Comparison(),Array.Empty<ModEntry>(),new string('c',64));prompt.Draw();
    var row=GUILayout.Toggles.Single(t=>t.Text.TrimStart().StartsWith("Optional: Child"));Check(!row.Selected&&row.Enabled);
    Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Child").TextColor==new Color(.914f,.780f,.490f,1));
});
Test("search and attention filters hide unrelated or matching rows",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-search030-"+Guid.NewGuid().ToString("N"));
    var wanted=new ModEntry{Guid="wanted",Name="Wanted",Version="1.0.0",Side="required"};var other=new ModEntry{Guid="other",Name="Other",Version="1.0.0",Side="required"};
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){wanted,other}},new Comparison(),Array.Empty<ModEntry>(),new string('d',64));
    typeof(Prompt).GetField("search",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(prompt,"Wanted");prompt.Draw();
    Check(GUILayout.Toggles.Any(t=>t.Text.Contains("Wanted"))&&GUILayout.Toggles.All(t=>!t.Text.Contains("Other")));
});
Test("upgraded saved profiles open and keep saved configs as the update default",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-upgrade030-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;var key=new string('e',64);
    var app=new ModEntry{Guid="app",Name="App",Version="1.0.0",Side="required"};
    Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Servers=new(){new SavedProfile{Slot="server-1",ServerKey=key,Name="Old",Mods=new(){app},ServerPolicy=new Policy{Protocol=4,Mods=new(){app}}}}});
    using var prompt=new Prompt(_=>{},()=>{},()=>{});ZInput.ManagerShortcut();prompt.Update();prompt.Draw();Check(GUI.Buttons.Any(b=>b.Text=="Open profile (server-1)"));
    prompt.Show(new Policy{Mods=new(){app}},new Comparison(),Array.Empty<ModEntry>(),key);GUI.Click="Configs";prompt.Draw();
    Check(GUILayout.Toggles.Any(t=>t.Text=="Keep this server's existing configs (recommended)"&&t.Selected));
});
Test("settings defaults password remembering on and exposes cache cleanup",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-settings030-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.ShowError("fixture");GUI.Click="Settings";prompt.Draw();
    Check(GUILayout.Toggles.Any(t=>t.Text=="Remember server passwords by default"&&t.Selected));
    Check(GUI.Buttons.Any(b=>b.Text=="Clear download cache"&&b.Enabled));
});
Test("activity copy error never requires scrolling the full log",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-error030-"+Guid.NewGuid().ToString("N"));
    using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy(),new Comparison(),Array.Empty<ModEntry>(),new string('f',64));
    typeof(Prompt).GetField("error",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(prompt,"Fixture stopped");GUI.Click="Copy error";prompt.Draw();
    Check(GUIUtility.systemCopyBuffer=="Fixture stopped");
});

Test("native password remember checkbox accepts Valheim mouse input while the main prompt is closed",()=>
{
    using var prompt=new Prompt(_=>{},()=>{},()=>{});var area=new Rect(20,Screen.height-122,470,40);
    Check(prompt.PasswordRememberToggle(area,true));ZInput.pointerPosition=new Vector3(area.x+10,Screen.height-area.y-10,0);
    ZInput.Pressed.Add(KeyCode.Mouse0);prompt.Update();ZInput.Released.Add(KeyCode.Mouse0);prompt.Update();Check(!prompt.PasswordRememberToggle(area,true)&&!prompt.IsVisible);
});
Test("back from review returns to selection while preserving checked optional choices",()=>
{
    BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-review-back030-"+Guid.NewGuid().ToString("N"));
    var optional=new ModEntry{Guid="option",Name="Option",Version="1.0.0",Side="optional"};using var prompt=new Prompt(_=>{},()=>{},()=>{});
    prompt.Show(new Policy{Mods=new(){optional}},new Comparison(),Array.Empty<ModEntry>(),new string('a',64));prompt.Draw();GUI.Click=GUILayout.Toggles.Single(t=>t.Text.TrimStart().StartsWith("Optional: Option")).Text;prompt.Draw();
    var flags=BindingFlags.NonPublic|BindingFlags.Instance;typeof(Prompt).GetField("prepared",flags)!.SetValue(prompt,new PendingInstall{Id=Guid.NewGuid().ToString("N")});typeof(Prompt).GetField("preparedProfile",flags)!.SetValue(prompt,new SavedProfile());
    GUI.Click="Back to selection";prompt.Draw();Check(prompt.IsVisible&&typeof(Prompt).GetField("prepared",flags)!.GetValue(prompt)==null&&((HashSet<string>)typeof(Prompt).GetField("selectedMods",flags)!.GetValue(prompt)!).Contains("option"));
});
Test("closing setup discards a late successful staging result without reopening review",()=>
{
    var root=Path.Combine(Path.GetTempPath(),"ModGate-dismiss-race030-"+Guid.NewGuid().ToString("N"));BepInEx.Paths.BepInExRootPath=root;using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.ShowError("fixture");
    var flags=BindingFlags.NonPublic|BindingFlags.Instance;var field=typeof(Prompt).GetField("task",flags)!;var type=field.FieldType.GenericTypeArguments[0];var completion=Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(type))!;
    field.SetValue(prompt,completion.GetType().GetProperty("Task")!.GetValue(completion));ZInput.Pressed.Add(KeyCode.Escape);prompt.Update();
    var id=Guid.NewGuid().ToString("N");var stage=SafePath.Under(root,"ModGate/staging/"+id);Directory.CreateDirectory(stage);File.WriteAllText(Path.Combine(stage,"fixture.txt"),"fixture");
    var result=Activator.CreateInstance(type,true)!;type.GetField("Install")!.SetValue(result,new PendingInstall{Id=id});completion.GetType().GetMethod("SetResult")!.Invoke(completion,new[]{result});prompt.Update();
    Check(!prompt.IsVisible&&typeof(Prompt).GetField("prepared",flags)!.GetValue(prompt)==null&&!Directory.Exists(stage));
});
Test("clientOnly mods are locked selected requirements with an explanatory label",()=>
{
 BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-clientonly031-"+Guid.NewGuid().ToString("N"));
 var wanted=new ModEntry{Guid="visual",Name="Visual",Version="1.0.0",Side="clientOnly"};
 using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{Mods=new(){wanted}},new Comparison(),Array.Empty<ModEntry>(),new string('c',64));prompt.Draw();
 var row=GUILayout.Toggles.Single(t=>t.Text.StartsWith("Required: Visual"));Check(row.Selected&&!row.Enabled);
 Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Visual").TextColor==new Color(.945f,.631f,.596f,1));
 Check(GUILayout.Labels.Any(l=>l.Contains("Client only")));
});
Test("allowed newer installed versions are green and identified as accepted",()=>
{
 BepInEx.Paths.BepInExRootPath=Path.Combine(Path.GetTempPath(),"ModGate-newer031-"+Guid.NewGuid().ToString("N"));
 var wanted=new ModEntry{Guid="visual",Name="Visual",Version="1.0.0",Side="clientOnly"};var installed=new ModEntry{Guid="visual",Name="Visual",Version="2.0.0"};
 using var prompt=new Prompt(_=>{},()=>{},()=>{});prompt.Show(new Policy{AllowNewerClientVersions=true,Mods=new(){wanted}},new Comparison(),new[]{installed},new string('d',64));
 typeof(Prompt).GetField("modFilter",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(prompt,"All");prompt.Draw();
 var row=GUILayout.Toggles.Single(t=>t.Text.StartsWith("Required: Visual"));Check(row.Selected&&!row.Enabled);
 Check(DrawCapture.Commands.Last(c=>c.Kind=="label"&&c.Text=="Visual").TextColor==new Color(.604f,.812f,.667f,1));
 Check(GUILayout.Labels.Any(l=>l.Contains("Newer accepted")));
});
Feature040Checks.Run(Test,Check);
AdminConfigUiChecks.Run(Test,Check);
Visual041Checks.Run(Test,Check);
Feature042Checks.Run(Test,Check);
ServerHealthUiChecks.Run(Test,Check);
Console.WriteLine("All " + passed + " prompt simulations passed. These do not render or run Valheim.");

}
catch (Exception failure) { Console.Error.WriteLine(failure); Environment.ExitCode = 1; }
