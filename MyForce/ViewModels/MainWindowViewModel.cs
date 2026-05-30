using System.Linq;
using System.Collections.ObjectModel;
using MyForce.Models;

namespace MyForce.ViewModels;

public sealed class MainWindowViewModel
{
    public MainWindowViewModel()
    {
        Components = new ObservableCollection<SystemComponent>
        {
            new("Audio Processor", "Audio + Control", "Central audio router, bridge engine, radio host, and config store.", "USB, MQTT", "Planned", true),
            new("SoundCard Interfaces", "Audio", "Per-radio audio and PTT interface modules between radios and the Audio Processor.", "USB", "Planned", true),
            new("Radio Modules", "Audio", "Plugin-based radio integrations for Barrett, XPR, MTM5400, APX/XTL, Harris, and 4W radios.", "USB, RS232, relay", "Planned", true),
            new("Operator Mic / Voice Control", "Audio", "Operator transmit audio source for manual PTT and bridge participation.", "USB", "Planned", true),
            new("Speaker / Car Speaker System", "Audio", "Vehicle receive audio output driven by the Audio Processor.", "USB / line out", "Planned", true),
            new("MQTT Broker", "Control", "Central bus for commands, retained config, state, and health messages.", "MQTT", "Planned", true),
            new("UI Handler", "Control", "Operator UI, dynamic admin surface, and CAD integration endpoint.", "MQTT, FreeRDP", "In Progress", true),
            new("GPIO Relay Controller", "Control", "Non-radio relay control for vehicle and auxiliary hardware.", "MQTT", "Planned", false),
            new("Siren Interface Controller", "Control", "MQTT bridge for the physical siren controller.", "MQTT, RS232 (TBD)", "Planned", false),
            new("SCADA Controller", "Control", "MQTT bridge for the vehicle SCADA system.", "MQTT, TBD", "Planned", false),
            new("Siren Controller", "External", "Physical siren hardware endpoint.", "RS232 (TBD)", "External", false),
            new("SCADA System", "External", "Vehicle SCADA endpoint.", "TBD", "External", false),
            new("CAD Windows PC", "External", "Remote dispatch workstation reached over the vehicle LAN.", "FreeRDP over LAN", "External", false),
        };

        Connections = new ObservableCollection<SystemConnection>
        {
            new("Radio", "SoundCard", "Line audio + keying", "Audio"),
            new("SoundCard", "Audio Processor", "USB", "Audio"),
            new("Operator Mic", "Audio Processor", "USB", "Audio"),
            new("Speaker", "Audio Processor", "USB / line out", "Audio"),
            new("Audio Processor", "MQTT Broker", "MQTT", "Control"),
            new("GPIO Relay Controller", "MQTT Broker", "MQTT", "Control"),
            new("Siren Interface Controller", "MQTT Broker", "MQTT", "Control"),
            new("SCADA Controller", "MQTT Broker", "MQTT", "Control"),
            new("UI Handler", "MQTT Broker", "MQTT", "Control"),
            new("UI Handler", "CAD Windows PC", "FreeRDP over vehicle LAN", "External"),
            new("Siren Interface Controller", "Siren Controller", "RS232 (TBD)", "External"),
            new("SCADA Controller", "SCADA System", "TBD", "External"),
        };

        TopicGroups = new ObservableCollection<MqttTopicGroup>
        {
            new("System Plugins", "myforce/sys/plugins", "Yes", "1", "Loaded plugin types published by the Audio Processor for add-module discovery."),
            new("System Definition", "myforce/sys/definition", "Yes", "1", "Declared module ids, types, and aliases mirrored from the config store."),
            new("Module Registry", "myforce/module/<id>/registry", "Yes", "1", "Schema and identity metadata used to build admin pages."),
            new("Module Config", "myforce/module/<id>/config", "Yes", "1", "Applied module configuration confirmed by the owner."),
            new("Module Status", "myforce/module/<id>/status", "Yes", "1", "Presence and health with birth/LWT handling."),
            new("Module State", "myforce/module/<id>/state", "Yes", "0-1", "Last-known runtime state such as RX/TX activity."),
            new("Module Commands", "myforce/module/<id>/cmd/<action>", "No", "1", "Desired module actions and configuration requests from the UI."),
            new("Console PTT", "myforce/console/cmd/ptt", "No", "1", "Manual transmit control routed to the Audio Processor."),
            new("Console Monitor", "myforce/console/state/monitor", "Yes", "1", "Speaker monitor routing snapshot."),
            new("Bridge Config", "myforce/bridge/<bridge_id>/config", "Yes", "1", "Bridge members, priorities, gains, and enabled state."),
            new("Bridge State", "myforce/bridge/<bridge_id>/state", "Yes", "1", "Current bridge activity and holder member."),
        };
    }

    public string Title => "MyForce Gateway Overview";

    public string Summary => "Initial application components derived from PROJECT_FRAMEWORK.md, covering the audio plane, control plane, external integrations, and MQTT topic taxonomy.";

    public int ComponentCount => Components.Count;

    public int CoreComponentCount => Components.Count(component => component.IsCore);

    public int ConnectionCount => Connections.Count;

    public int TopicGroupCount => TopicGroups.Count;

    public ObservableCollection<SystemComponent> Components { get; }

    public ObservableCollection<SystemConnection> Connections { get; }

    public ObservableCollection<MqttTopicGroup> TopicGroups { get; }
}
