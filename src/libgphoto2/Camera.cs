using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public class Camera
{
    private readonly libgphoto2Driver _driver = new();
    private readonly CameraCommandInterpreter _interpreter;

    public IsoProperty Iso { get; }
    public ShutterProperty shutterSpeed { get; }
    public ApertureProperty aperture { get; }
    public FocusProperty focus { get; }

    private bool _liveViewActive = false;

    public Camera()
    {
        _interpreter = new CameraCommandInterpreter(_driver);
        
        Iso = new IsoProperty(this);
        shutterSpeed = new ShutterProperty(this);
        aperture = new ApertureProperty(this);
        focus = new FocusProperty(this);
    }

    public class FocusProperty
    {
        private readonly Camera _owner;
        internal FocusProperty(Camera owner) { _owner = owner; }

        public string mode
        {
            get => _owner._driver.GetWidgetInfo("focusmode")?.CurrentValue ?? "UNKNOWN";
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

        public (float Min, float Max, float Step)? GetManualFocusDriveRange()
        {
            if(!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
            return _owner._driver.GetWidgetInfo("manualfocusdrive")?.Range;
        }

        public void Closer(int step)
        {
            if(step < 0) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be negative.");
            if(step > GetManualFocusDriveRange()?.Max) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be greater than the maximum range.");
            _owner._interpreter.Execute("FocusCloser", step);
        }

        public void Further(int step)
        {
            if(step < 0) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be negative.");
            if(step > GetManualFocusDriveRange()?.Max) throw new ArgumentOutOfRangeException(nameof(step), "Focus step cannot be greater than the maximum range.");
            _owner._interpreter.Execute("FocusFurther", step);
        }
    }

    public class IsoProperty {
        private readonly Camera _owner;
        internal IsoProperty(Camera owner) { _owner = owner; }

        private List<string>? _cachedValues;
        private readonly object _cacheLock = new();

        public int value {
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                return int.TryParse(_owner._driver.GetWidgetInfo("iso")?.CurrentValue, out var v) ? v : 0;
            }
            set{
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                if(value < 0) throw new ArgumentOutOfRangeException(nameof(value), "ISO value cannot be negative.");
                //if(!_owner.Iso.Values.Contains(value.ToString()))
                //    throw new ArgumentException($"ISO value '{value}' is not supported by the camera.", nameof(value));
                 _owner._driver.SetWidgetValueByPath("iso", value.ToString());
            }
        }

        public int index {
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                var info = _owner._driver.GetWidgetInfo("iso");
                if (info?.Choices == null) return -1;
                var cur = info.CurrentValue ?? "";
                for (int i = 0; i < info.Choices.Count; i++)
                    if (string.Equals(info.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            set {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                _owner._driver.SetWidgetValueByChoiceIndex("iso", value);
            }
        }

        public IReadOnlyList<string> Values
        {
            get
            {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
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
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                return _owner._driver.GetWidgetInfo("shutterspeed")?.CurrentValue ?? "";
            }
            set { 
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                //if (!_owner.shutterSpeed.Values.Contains(value))
                //    throw new ArgumentException($"Shutter Speed value '{value}' is not supported by the camera.", nameof(value));
                _owner._driver.SetWidgetValueByPath("shutterspeed", value); 
                }
        }

        public int index {
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                var info = _owner._driver.GetWidgetInfo("shutterspeed");
                if (info?.Choices == null) return -1;
                var cur = info.CurrentValue ?? "";
                for (int i = 0; i < info.Choices.Count; i++)
                    if (string.Equals(info.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            set {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                _owner._driver.SetWidgetValueByChoiceIndex("shutterspeed", value);
            }
        }

        public IReadOnlyList<string> Values {
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
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
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                return _owner._driver.GetWidgetInfo("f-number")?.CurrentValue ?? "";
            }
            set { 
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                //if (!_owner.aperture.Values.Contains(value) && !_owner.aperture.Values.Contains($"f/{value}") && !_owner.aperture.Values.Contains($"f{value}") && !_owner.aperture.Values.Contains($"f {value}"))
                //    throw new ArgumentException($"Aperture value '{value}' is not supported by the camera.", nameof(value));
                _owner._driver.SetWidgetValueByPath("f-number", value); 
                }
        }

        public int index {
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                var info = _owner._driver.GetWidgetInfo("f-number");
                if (info?.Choices == null) return -1;
                var cur = info.CurrentValue ?? "";
                for (int i = 0; i < info.Choices.Count; i++)
                    if (string.Equals(info.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            set {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
                _owner._driver.SetWidgetValueByChoiceIndex("f-number", value);
            }
        }

        public IReadOnlyList<string> Values {
            get {
                if (!_owner._interpreter.HasCapability("Configuration")) throw new NotSupportedException("Configuration capability is disabled.");
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

    public string batteryLevel => _driver.GetWidgetInfo("batterylevel")?.CurrentValue ?? "UNKNOWN";

    public string cameramodel => _driver.GetWidgetInfo("cameramodel")?.CurrentValue ?? "UNKNOWN";

    public string manufacturer => _driver.GetWidgetInfo("manufacturer")?.CurrentValue ?? "UNKNOWN";

    public bool connected => _driver.IsCameraConnected();

    public IEnumerable<AvailableCamera> GetAvailableCameras() {
        return _driver.GetAvailableCameras();
    }

    public void Magnify()
    {
        _interpreter.Execute("Magnify");
    }

    public void Magnify2x()
    {
        _interpreter.Execute("Magnify2x");
    }

    public void MagnifyOff()
    {
        _interpreter.Execute("MagnifyOff");
    }
    public void ConnectCamera(AvailableCamera cam) {
        _driver.SelectCamera(cam.Port, cam.Model);
        _driver.EnsureInitialized();
        LoadConfigs();
    }

    private void LoadConfigs()
    {
        _interpreter.Clear();
        
        string baseDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "libgphoto2", "configs");
        if (!System.IO.Directory.Exists(baseDir))
        {
            baseDir = "src/libgphoto2/configs";
        }

        // Priority 1: Model specific
        string model = SanitizeFilename(this.cameramodel);
        if (!string.IsNullOrEmpty(model))
        {
             string modelPath = System.IO.Path.Combine(baseDir, "specific", $"{model}.conf");
             if (System.IO.File.Exists(modelPath))
             {
                 Console.WriteLine($"Loading config: {modelPath}");
                 _interpreter.Load(modelPath);
                 return;
             }
        }

        // Priority 2: Make specific
        string make = SanitizeFilename(this.manufacturer);
        if (!string.IsNullOrEmpty(make))
        {
            string makePath = System.IO.Path.Combine(baseDir, "specific", $"{make}_default.conf");
            if (System.IO.File.Exists(makePath))
            {
                Console.WriteLine($"Loading config: {makePath}");
                _interpreter.Load(makePath);
                return;
            }

            string simpleMake = make.Split(' ')[0];
            if (make != simpleMake)
            {
                string simpleMakePath = System.IO.Path.Combine(baseDir, "specific", $"{simpleMake}_default.conf");
                if (System.IO.File.Exists(simpleMakePath))
                {
                    Console.WriteLine($"Loading config: {simpleMakePath}");
                    _interpreter.Load(simpleMakePath);
                    return;
                }
                else
                {
                    Console.WriteLine($"No specific config found for make: {simpleMakePath}");
                }
            }
        }

        // Priority 3: Default
        string defaultPath = System.IO.Path.Combine(baseDir, "default_commands.conf");
        if (System.IO.File.Exists(defaultPath))
        {
            Console.WriteLine($"Loading config: {defaultPath}");
            _interpreter.Load(defaultPath);
        }
        else
        {
            Console.WriteLine("Warning: No configuration file found.");
        }
    }

    private static string SanitizeFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Trim();
    }

    public void ClearSelectedCamera() {
        _driver.ClearSelectedPort();
    }

    // NOTE: CaptureImage cannot be used to capture bulb exposures, the length of the exposure will be undefined!
    public string CaptureImage(string outputPath) {
        if (!_interpreter.HasCapability("ImageCapture"))
            throw new NotSupportedException("Image capture is not supported by the current camera configuration.");
        if (_liveViewActive) {
            //TODO: CHECK ON A DSLR HOW THIS ACTUALLY WORKS
            _interpreter.Execute("StopLiveView");
            _liveViewActive = false;
        }
        return _driver.Capture(outputPath);
    }

    public string CaptureImageBulb(float timeSec, string outputPath) {
        if (!_interpreter.HasCapability("TriggerCapture"))
            throw new NotSupportedException("Bulb/Trigger capture is not supported by the current camera configuration.");
        if (timeSec < 0) throw new ArgumentOutOfRangeException(nameof(timeSec), "Time cannot be negative.");

        if (_liveViewActive) {
            //TODO: CHECK ON A DSLR HOW THIS ACTUALLY WORKS
            _interpreter.Execute("StopLiveView");
            _liveViewActive = false;
        }

        _interpreter.Execute("StartBulb");
        Thread.Sleep((int)(timeSec * 1000));
        _interpreter.Execute("StopBulb");
        Console.WriteLine("Capture complete, waiting for image to be available...");

        int maxWait = 30000; // 30 seconds timeout
        int elapsed = 0;
        while (elapsed < maxWait)
        {
            var cameraPath = _driver.WaitForImage(1000); // Wait 1s at a time
            if (cameraPath != null)
            {
                string extension = System.IO.Path.GetExtension(cameraPath).ToLower();
                string folder = System.IO.Path.GetDirectoryName(cameraPath)?.Replace("\\", "/") ?? "/";
                string filename = System.IO.Path.GetFileName(cameraPath);
                _driver.DownloadFile(folder, filename, outputPath + extension);
                return outputPath + extension;
            }
            elapsed += 1000;
        }
        throw new TimeoutException("Timed out waiting for image to be saved by camera.");
    }


    public void Shutdown() {
        _driver.Shutdown();
    }
    
    public byte[] GetLiveViewBytes() {
        if (!_interpreter.HasCapability("LiveView"))
            throw new NotSupportedException("Live View is not supported by the current camera configuration.");

        if (!_liveViewActive) {
            //TODO: CHECK ON A DSLR HOW THIS ACTUALLY WORKS
            _interpreter.Execute("StartLiveView");
            _liveViewActive = true;
        }
        return _driver.CapturePreviewBytes();
    }
    
    public void SaveLiveView(string path) {
        if (!_interpreter.HasCapability("LiveView"))
            throw new NotSupportedException("Live View is not supported by the current camera configuration.");
        _driver.CapturePreviewToFile(path);
    }
}