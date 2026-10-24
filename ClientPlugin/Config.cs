using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using ClientPlugin.Settings.Elements;
using ClientPlugin.Settings.Tools;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private bool enabled = true;
    private bool showMessages = true;
    private bool pickColor = true;
    private bool addMissingColor = false;
    private bool equipWithoutToolbar = false;
    // 0x47 = the G key. Default is Ctrl + G.
    private Binding pickKey = new Binding(0x47, ctrl: true);

    #endregion

    #region User interface

    [XmlIgnore]
    public readonly string Title = "Block Picker";

    [Separator("Block Picker")]

    [Checkbox(description: "Turn the plugin on or off")]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    [Keybind(description: "Aim at a block while holding a block, press this to select that block. Right click the button to unbind.")]
    public Binding PickKey
    {
        get => pickKey;
        set => SetField(ref pickKey, value);
    }

    [Checkbox(description: "Also select the paint color of the picked block (if that color is in your palette)")]
    public bool PickColor
    {
        get => pickColor;
        set => SetField(ref pickColor, value);
    }

    [Checkbox(description: "If the picked color is not in your palette, put it into your palette (replaces the palette color that is currently selected)")]
    public bool AddMissingColor
    {
        get => addMissingColor;
        set => SetField(ref addMissingColor, value);
    }

    [Checkbox(description: "Hold the picked block without selecting or adding it on your build bar (your build bar is left untouched)")]
    public bool EquipWithoutToolbar
    {
        get => equipWithoutToolbar;
        set => SetField(ref equipWithoutToolbar, value);
    }

    [Checkbox(description: "Show a small popup message when picking (or when the block is not on your build bar)")]
    public bool ShowMessages
    {
        get => showMessages;
        set => SetField(ref showMessages, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
