namespace VTTranslate.Core.Audio;

public enum AudioDeviceKind { Unknown, BuiltIn, Usb, Bluetooth, Virtual }

public sealed record AudioDeviceInfo(string Id, string Name, bool IsInput, bool IsDefault, AudioDeviceKind Kind = AudioDeviceKind.Unknown);
