using System.Diagnostics;
using ModGate;
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var powershell=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
string Literal(string value)=>"'"+value.Replace("'","''")+"'";
Process Fixture()=>Process.Start(new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds 1800\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})!;
string Child(string marker,string version,bool ready)
{
 return "$snapshot = @{ Initialized = $env:DOORSTOP_INITIALIZED; Disabled = $env:DOORSTOP_DISABLE; Target = $env:DOORSTOP_TARGET_ASSEMBLY; Manager = $env:MODGATE_FIXTURE_KEEP }; "
 +"[System.IO.File]::WriteAllText("+Literal(marker)+", ($snapshot | ConvertTo-Json -Compress)); "
 +"$confirmation = Join-Path $env:MODGATE_RESTART_ROOT ('ModGate/restart-confirmed-' + $env:MODGATE_RESTART_TOKEN + '.json'); "
 +"$result = @{ Version = "+Literal(version)+"; Ready = "+(ready?"$true":"$false")+"; Error = 'fixture profile remains pending' }; "
 +"[System.IO.File]::WriteAllText($confirmation, ($result | ConvertTo-Json -Compress));";
}
void Wait(Process helper,string data,bool success)
{
 if(!helper.WaitForExit(30000))throw new Exception("Helper timeout");
 var error=Path.Combine(data,"ModGate","restart-error.txt");
 if(success && helper.ExitCode!=0)throw new Exception(File.Exists(error)?File.ReadAllText(error):"No diagnostic");
 if(!success && helper.ExitCode==0)throw new Exception("Unexpected success");
}
var originalFlag=Environment.GetEnvironmentVariable("DOORSTOP_INITIALIZED");var disabled=Environment.GetEnvironmentVariable("DOORSTOP_DISABLE");var target=Environment.GetEnvironmentVariable("DOORSTOP_TARGET_ASSEMBLY");var manager=Environment.GetEnvironmentVariable("MODGATE_FIXTURE_KEEP");
try
{
 Environment.SetEnvironmentVariable("DOORSTOP_INITIALIZED","TRUE");Environment.SetEnvironmentVariable("DOORSTOP_DISABLE","TRUE");Environment.SetEnvironmentVariable("DOORSTOP_TARGET_ASSEMBLY","manager-loader");Environment.SetEnvironmentVariable("MODGATE_FIXTURE_KEEP","active manager profile");
 var marker=Path.Combine(root,"restarted.json");var bootstrap=Path.Combine(root,"fixture-preloader.dll");File.WriteAllText(bootstrap,"fixture only");
 using var original=Fixture();
 var plan=new RestartPlan{ProcessId=original.Id,StartTicks=original.StartTime.ToUniversalTime().Ticks,Executable=powershell,Arguments="-NoProfile -NonInteractive -Command "+RestartLauncher.QuoteArgument(Child(marker,Constants.Version,true)),WorkingDirectory=root,BootstrapAssembly=bootstrap,ConfirmationId=Guid.NewGuid().ToString("N"),ExpectedVersion=Constants.Version};
 using(var helper=RestartLauncher.Start(root,plan))
 {
  if(File.Exists(marker))throw new Exception("Launched before fixture exit");Wait(helper,root,true);
 }
 var snapshot=System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker)).RootElement;
 if(snapshot.GetProperty("Initialized").ValueKind!=System.Text.Json.JsonValueKind.Null||snapshot.GetProperty("Disabled").ValueKind!=System.Text.Json.JsonValueKind.Null||snapshot.GetProperty("Target").GetString()!="manager-loader"||snapshot.GetProperty("Manager").GetString()!="active manager profile")throw new Exception("Bad inherited environment");
 if(!File.ReadAllText(Path.Combine(root,"ModGate","restart-status.txt")).Contains("Confirmed:")||!original.HasExited)throw new Exception("Missing confirmation or wrong shutdown ordering");
 Console.WriteLine("PASS actual hidden helper clears inherited Doorstop guard flags, retains manager configuration, waits for fixture shutdown and requires child confirmation");
 foreach(var ready in new[]{true,false})
 {
  var data=Path.Combine(root,ready?"wrong-version":"pending-profile");Directory.CreateDirectory(Path.Combine(data,"ModGate"));
  var request=Path.Combine(data,"ModGate","profile-request.json");File.WriteAllText(request,"pending fixture");
  using var parent=Fixture();plan.ProcessId=parent.Id;plan.StartTicks=parent.StartTime.ToUniversalTime().Ticks;plan.ConfirmationId=Guid.NewGuid().ToString("N");
  plan.Arguments="-NoProfile -NonInteractive -Command "+RestartLauncher.QuoteArgument(Child(Path.Combine(data,"started.json"),ready?"0.0.0":Constants.Version,ready));
  using var helper=RestartLauncher.Start(data,plan);Wait(helper,data,false);
  var message=File.ReadAllText(Path.Combine(data,"ModGate","restart-error.txt"));
  if(!message.Contains(ready?"different ModGate":"fixture profile remains pending")||File.ReadAllText(request)!="pending fixture")throw new Exception("Failed confirmation was lost or changed request");
  Console.WriteLine("PASS actual helper rejects "+(ready?"wrong loaded version":"unfinished profile")+" and preserves its scheduled request");
 }
 using var live=Fixture();var mismatch=Path.Combine(root,"mismatch");Directory.CreateDirectory(mismatch);plan.ProcessId=live.Id;plan.StartTicks=live.StartTime.ToUniversalTime().Ticks-1;
 using var blocked=RestartLauncher.Start(mismatch,plan);Wait(blocked,mismatch,false);
 if(!File.ReadAllText(Path.Combine(mismatch,"ModGate","restart-error.txt")).Contains("identity changed"))throw new Exception("Wrong PID diagnostic");
 Console.WriteLine("PASS mismatched process identity is rejected without terminating the original fixture");
}
finally
{
 Environment.SetEnvironmentVariable("DOORSTOP_INITIALIZED",originalFlag);Environment.SetEnvironmentVariable("DOORSTOP_DISABLE",disabled);Environment.SetEnvironmentVariable("DOORSTOP_TARGET_ASSEMBLY",target);Environment.SetEnvironmentVariable("MODGATE_FIXTURE_KEEP",manager);
}
