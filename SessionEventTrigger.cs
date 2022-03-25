using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using ScreenConnect;
using System.Linq;
using System.Collections;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Reflection;
public class SessionEventTriggerAccessor : IDynamicSessionEventTrigger
{
	public Proc GetDeferredActionIfApplicable(SessionEventTriggerEvent sessionEventTriggerEvent)
	{
		if (
			sessionEventTriggerEvent.SessionEvent.EventType == SessionEventType.Connected 
			&& sessionEventTriggerEvent.SessionConnection.ProcessType == ProcessType.Guest 
			&& ExtensionContext.Current.GetSettingValue("MaintenanceMode") == "0" 
			&& sessionEventTriggerEvent.Session.ActiveConnections.Where(_ => _.ProcessType == ProcessType.Host).Count() == 0
		) {
			return () => ScreenConnect.TaskExtensions.InvokeSync(async delegate {
				 RunDiagnostics(sessionEventTriggerEvent, ExtensionContext.Current); 
			});
		}
		else if (
			sessionEventTriggerEvent.SessionEvent.EventType == SessionEventType.RanCommand 
			&& IsDiagnosticContent(sessionEventTriggerEvent.SessionEvent.Data)
		) {
			return () => ScreenConnect.TaskExtensions.InvokeSync(async delegate
			{
				try
					{
						var sessionDetails = SessionManagerPool.Demux.GetSessionDetailsAsync(sessionEventTriggerEvent.Session.SessionID);
	                    string output = sessionEventTriggerEvent.SessionEvent.Data;
	                    
						if (IsDiagResult(output)) {
							var data = output.Split(new string[] { "!---BEGIN JSON---!" }, StringSplitOptions.None);
							if (data[1] != "") {
								DiagOutput diag = Deserialize(data[1]);
								var session = sessionEventTriggerEvent.Session;
								if (diag.version != null) {
									session.CustomPropertyValues[Int32.Parse(ExtensionContext.Current.GetSettingValue("AgentVersionCustomProperty")) - 1] = diag.version;
								}
								if (diag.id != null) {
									session.CustomPropertyValues[Int32.Parse(ExtensionContext.Current.GetSettingValue("AgentIDCustomProperty")) - 1] = diag.id;
								}
								var sessionname = session.Name;
								if (ExtensionContext.Current.GetSettingValue("SetUseMachineName") == "1") {
									sessionname = "";
								}
								await SessionManagerPool.Demux.UpdateSessionAsync("AutomateDiagnostics", session.SessionID, sessionname, session.IsPublic, session.Code, session.CustomPropertyValues);
							}
						}
						else if (IsRepairResult(output)) {
							RunDiagnostics(sessionEventTriggerEvent, ExtensionContext.Current);
						}
					} 
					catch
					{
						//var session = sessionEventTriggerEvent.Session;
						//session.CustomPropertyValues[Int32.Parse(ExtensionContext.Current.GetSettingValue("AgentVersionCustomProperty")) - 1] = "Caught";
						//don't care atm
					}
			});
		}
		return null;
	}
	
	public DiagOutput Deserialize(string json) {
        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(DiagOutput));
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) {
        	return ser.ReadObject(ms) as DiagOutput;
        }
    }
	private async void RunDiagnostics(SessionEventTriggerEvent sessionEventTriggerEvent, ExtensionContext extensionContext) {
		var sessionDetails = await SessionManagerPool.Demux.GetSessionDetailsAsync(sessionEventTriggerEvent.Session.SessionID);
		if (sessionDetails.Session.SessionType == SessionType.Access) {
			var ltposh = extensionContext.GetSettingValue("PathToLTPoSh");
			var diag = extensionContext.GetSettingValue("PathToDiag");
			var linuxdiag = extensionContext.GetSettingValue("PathToMacLinuxDiag");
			var server = extensionContext.GetSettingValue("AutomateHostname");
			var os = sessionDetails.Session.GuestInfo.OperatingSystemName;
			var timeout = extensionContext.GetSettingValue("Timeout");
			var command = "";

			if ( os.Contains("Windows") ) { 
				command = "#!ps\n#maxlength=100000\n#timeout="+ timeout +"\necho 'DIAGNOSTIC-RESPONSE/1'\necho 'DiagnosticType: Automate'\necho 'ContentType: json'\necho ''\n$WarningPreference='SilentlyContinue'; IF([Net.SecurityProtocolType]::Tls) {[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls}; IF([Net.SecurityProtocolType]::Tls11) {[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls11}; IF([Net.SecurityProtocolType]::Tls12) {[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12}; Try {(new-object Net.WebClient).DownloadString('"+ diag +"') | iex; Start-AutomateDiagnostics -ltposh '"+ ltposh +"' -automate_server '"+server+"'} Catch { $_.Exception.Message; Write-Output '!---BEGIN JSON---!'; Write-Output '{\"version\": \"Error loading AutomateDiagnostics\"}' }";
			}
			else if ( os.Contains("Mac") || os.Contains("Linux") ) {
				command = "#!sh\n#maxlength=100000\n#timeout="+ timeout +"\necho 'DIAGNOSTIC-RESPONSE/1'\necho 'DiagnosticType: Automate'\necho 'ContentType: json'\nurl="+linuxdiag+"; CURL=$(command -v curl); WGET=$(command -v wget); if [ ! -z $CURL ]; then echo $($CURL -s $url | python); else echo $($WGET -q -O - --no-check-certificate $url | python); fi";
			}
			else { command = "@echo off\necho No OS Detected, try running the diagnostic again"; }
			
			await SessionManagerPool.Demux.AddSessionEventAsync(
				sessionEventTriggerEvent.Session.SessionID,
				SessionEventType.QueuedCommand,
				SessionEventAttributes.NeedsProcessing,
				"AutomateDiagnostics",
				command
			);
		}
	}
	private bool IsDiagnosticContent(string eventData) {
		if (eventData.Contains("DIAGNOSTIC-RESPONSE/1") || eventData.Contains("\ufeffDIAGNOSTIC-RESPONSE/1")) {
			return true;
		} 
		else { 
			return false; 
		}
	}
	private bool IsRepairResult(string eventData) {
		if (eventData.Contains("DiagnosticType: ReinstallAutomate") || eventData.Contains("DiagnosticType: RestartAutomate")) {
			return true;
		}
		else { 
			return false; 
		}
	}
	private bool IsDiagResult(string eventData) {
		if (eventData.Contains("DiagnosticType: Automate")) {
			return true;
		}
		else { 
			return false; 
		}
	}
    private static string FormatMessage(string message) {
        DateTime now = DateTime.Now;
        return string.Format("{0}: {1}", now.ToString(), message);
    }
	public static void WriteLog(string message) {
        try {
            using (StreamWriter streamWriter = new StreamWriter(string.Concat(Environment.ExpandEnvironmentVariables("%windir%"), "\\temp\\AutomateDiagnostics.log"), true)) {
                streamWriter.WriteLine(FormatMessage(message));
            }
        }
        catch {}
    }
	public static void var_dump(object obj)   
	{   
			WriteLog(String.Format("{0,-18} {1}", "Name", "Value"));   
			string ln = @"-----------------------------------------------------------------";   
			WriteLog(ln);   
				
			Type t = obj.GetType();   
			PropertyInfo[] props = t.GetProperties();   
				
			for(int i = 0; i < props.Length; i++)   
			{   
					try   
					{   
							WriteLog(String.Format("{0,-18} {1}",   
								props[i].Name, props[i].GetValue(obj, null)));   
					}   
					catch(Exception e)   
					{   
							Console.WriteLine(e);   
					}   
			}   
	}
}

public class DiagOutput
{
    [DataMember(Name="id", IsRequired=false)]
    public String id;

    [DataMember(Name = "version", IsRequired = false)]
    public String version;

    [DataMember(Name = "server_addr", IsRequired = false)]
    public String server_addr;

    [DataMember(Name = "online", IsRequired = false)]
    public Boolean online;

    [DataMember(Name = "heartbeat", IsRequired = false)]
    public Boolean heartbeat;

    [DataMember(Name = "lastcontact", IsRequired = false)]
    public String lastcontact;

    [DataMember(Name = "heartbeat_sent", IsRequired = false)]
    public String heartbeat_sent;

    [DataMember(Name = "heartbeat_rcv", IsRequired = false)]
    public String heartbeat_rcv;

    [DataMember(Name = "ltposh_loaded", IsRequired = true)]
    public Boolean ltposh_loaded;
}