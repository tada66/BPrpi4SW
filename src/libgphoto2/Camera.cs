using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public class Camera
{
    private readonly libgphoto2Driver _driver = new();

    public IsoProperty Iso { get; }
    public ShutterProperty shutterSpeed { get; }
    public ApertureProperty aperture { get; }
    public Focus focus { get; }

    public Camera()
    {
        Iso = new IsoProperty(this);
        shutterSpeed = new ShutterProperty(this);
        aperture = new ApertureProperty(this);
        focus = new Focus(this);
    }

    public class Focus
    {
        private readonly Camera _owner;
        internal Focus(Camera owner) { _owner = owner; }

        public string mode
        {
            get => _owner._driver.GetWidgetInfo("focusmode")?.CurrentValue ?? "";
            set
            {
                if (!_owner.focus.Values.Contains(value))
                    throw new ArgumentException($"Focus Mode value '{value}' is not supported by the camera.", nameof(value));
                _owner._driver.SetWidgetValueByPath("focusmode", value);
            }
        }

        public IReadOnlyList<string> Values
        {
            get
            {
                var choices = _owner._driver.GetWidgetInfo("focusmode")?.Choices;
                return choices != null ? new List<string>(choices) : new List<string>();
            }
        }

        public void Closer(int step)
        {
            if(step < 0) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be negative.");
            if(step > 7) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be greater than 7.");
            _owner._driver.SetWidgetValueByPath("manualfocus", (-step).ToString());
        }

        public void Further(int step)
        {
            if(step < 0) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be negative.");
            if(step > 7) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be greater than 7.");
            _owner._driver.SetWidgetValueByPath("manualfocus", step.ToString());
        }
    }

    public class IsoProperty {
        private readonly Camera _owner;
        internal IsoProperty(Camera owner) { _owner = owner; }

        private List<string>? _cachedValues;
        private readonly object _cacheLock = new();

        public int value {
            get => int.TryParse(_owner._driver.GetWidgetInfo("iso")?.CurrentValue, out var v) ? v : 0;
            set{
                if(value < 0) throw new ArgumentOutOfRangeException(nameof(value), "ISO value cannot be negative.");
                if(!_owner.Iso.Values.Contains(value.ToString()))
                    throw new ArgumentException($"ISO value '{value}' is not supported by the camera.", nameof(value));
                 _owner._driver.SetWidgetValueByPath("iso", value.ToString());
            }
        }

        public int index {
            get {
                var info = _owner._driver.GetWidgetInfo("iso");
                if (info?.Choices == null) return -1;
                var cur = info.CurrentValue ?? "";
                for (int i = 0; i < info.Choices.Count; i++)
                    if (string.Equals(info.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            set => _owner._driver.SetWidgetValueByChoiceIndex("iso", value);
        }

        public IReadOnlyList<string> Values
        {
            get
            {
                if (_cachedValues != null) return _cachedValues;
                lock (_cacheLock)
                {
                    if (_cachedValues != null) return _cachedValues;
                    var choices = _owner._driver.GetWidgetInfo("iso")?.Choices;
                    _cachedValues = choices != null ? new List<string>(choices) : new List<string>();
                    return _cachedValues;
                }
            }
        }
        public void RefreshValues()
        {
            lock (_cacheLock) { _cachedValues = null; }
        }
    }
    
    public class ShutterProperty {
        private readonly Camera _owner;
        internal ShutterProperty(Camera owner) { _owner = owner; }
        private List<string>? _cachedValues;
        private readonly object _cacheLock = new();

        public string value {
            get => _owner._driver.GetWidgetInfo("shutterspeed")?.CurrentValue ?? "";
            set { 
                if (!_owner.shutterSpeed.Values.Contains(value))
                    throw new ArgumentException($"Shutter Speed value '{value}' is not supported by the camera.", nameof(value));
                _owner._driver.SetWidgetValueByPath("shutterspeed", value); 
                }
        }

        public int index {
            get {
                var info = _owner._driver.GetWidgetInfo("shutterspeed");
                if (info?.Choices == null) return -1;
                var cur = info.CurrentValue ?? "";
                for (int i = 0; i < info.Choices.Count; i++)
                    if (string.Equals(info.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            set => _owner._driver.SetWidgetValueByChoiceIndex("shutterspeed", value);
        }

        public IReadOnlyList<string> Values {
            get {
                if (_cachedValues != null) return _cachedValues;
                lock (_cacheLock) {
                    if (_cachedValues != null) return _cachedValues;
                    var choices = _owner._driver.GetWidgetInfo("shutterspeed")?.Choices;
                    _cachedValues = choices != null ? new List<string>(choices) : new List<string>();
                    return _cachedValues;
                }
            }
        }

        public void RefreshValues() { lock (_cacheLock) { _cachedValues = null; } }
    }

    public class ApertureProperty {
        private readonly Camera _owner;
        internal ApertureProperty(Camera owner) { _owner = owner; }
        private List<string>? _cachedValues;
        private readonly object _cacheLock = new();

        public string value {
            get => _owner._driver.GetWidgetInfo("f-number")?.CurrentValue ?? "";
            set { 
                if (!_owner.aperture.Values.Contains(value) && !_owner.aperture.Values.Contains($"f/{value}") && !_owner.aperture.Values.Contains($"f{value}") && !_owner.aperture.Values.Contains($"f {value}"))
                    throw new ArgumentException($"Aperture value '{value}' is not supported by the camera.", nameof(value));
                _owner._driver.SetWidgetValueByPath("f-number", value); 
                }
        }

        public int index {
            get {
                var info = _owner._driver.GetWidgetInfo("f-number");
                if (info?.Choices == null) return -1;
                var cur = info.CurrentValue ?? "";
                for (int i = 0; i < info.Choices.Count; i++)
                    if (string.Equals(info.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            set => _owner._driver.SetWidgetValueByChoiceIndex("f-number", value);
        }

        public IReadOnlyList<string> Values {
            get {
                if (_cachedValues != null) return _cachedValues;
                lock (_cacheLock) {
                    if (_cachedValues != null) return _cachedValues;
                    var choices = _owner._driver.GetWidgetInfo("f-number")?.Choices;
                    _cachedValues = choices != null ? new List<string>(choices) : new List<string>();
                    return _cachedValues;
                }
            }
        }

        public void RefreshValues() { lock (_cacheLock) { _cachedValues = null; } }
    }

    public string rawExtension {
        get; set;
    } = ".dng";

    public string batteryLevel => _driver.GetWidgetInfo("batterylevel")?.CurrentValue ?? "UNKNOWN";

    public string cameramodel => _driver.GetWidgetInfo("cameramodel")?.CurrentValue ?? "UNKNOWN";

    public string manufacturer => _driver.GetWidgetInfo("manufacturer")?.CurrentValue ?? "UNKNOWN";

    public IEnumerable<AvailableCamera> GetAvailableCameras() {
        return _driver.GetAvailableCameras();
    }

    public void Magnify()
    {
        ToggleSetting("focusmagnify");
        Thread.Sleep(250);
        ToggleSetting("focusmagnify");
    }

    public void Magnify2x()
    {
        Magnify();
        Thread.Sleep(250);
        ToggleSetting("focusmagnify");
    }

    public void MagnifyOff()
    {
        _driver.SetWidgetValueByPath("focusmagnify", "0");
        ToggleSetting("focusmagnifyexit");
    }
    public void ConnectCamera(string port) {
        _driver.SelectCamera(port);
        _driver.EnsureInitialized();
    }

    public string CaptureImage(string outputPath) {
        if(_driver.GetWidgetInfo("imagequality")?.CurrentValue == "RAW"){
            _driver.Capture(outputPath + rawExtension);
            return outputPath + rawExtension;
        }
        else
        {
            _driver.Capture(outputPath + ".jpg");
            return outputPath + ".jpg";
        }
    }

    public void Shutdown() {
        _driver.Shutdown();
    }
    
    public byte[] GetLiveViewBytes()
    {
        return _driver.CapturePreviewBytes();
    }
    
    public void SaveLiveView(string path)
    {
        _driver.CapturePreviewToFile(path);
    }

    private void ToggleSetting(string settingPath)
    {
        _driver.SetWidgetValueByPath(settingPath, "1");
        Thread.Sleep(50);
        _driver.SetWidgetValueByPath(settingPath, "0");
    }
}