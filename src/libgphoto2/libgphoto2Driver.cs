using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;

public record AvailableCamera(string Model, string Port);

internal class libgphoto2Driver
{
    private const string Lib = "libgphoto2.so.6";

    private IntPtr _ctx = IntPtr.Zero;
    private IntPtr _cam = IntPtr.Zero;
    private readonly object _sync = new();
    private bool _initialized;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct CameraFilePath
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string folder;
    }

    internal enum GPCaptureType : int { Image = 0 }
    internal enum GPFileType : int { Normal = 1 }

    internal enum GPResult : int
    {
        GP_OK = 0,
        GP_ERROR = -1,
        GP_ERROR_BAD_PARAMETERS = -2,
        GP_ERROR_NO_MEMORY = -3,
        GP_ERROR_LIBRARY = -4,
        GP_ERROR_UNKNOWN_PORT = -5,
        GP_ERROR_NOT_SUPPORTED = -6,
        GP_ERROR_IO = -7,
        GP_ERROR_FIXED_LIMIT_EXCEEDED = -8,
        GP_ERROR_TIMEOUT = -10,
        GP_ERROR_IO_SUPPORTED_SERIAL = -20,
        GP_ERROR_IO_SUPPORTED_USB = -21,
        GP_ERROR_IO_INIT = -31,
        GP_ERROR_IO_READ = -34,
        GP_ERROR_IO_WRITE = -35,
        GP_ERROR_IO_UPDATE = -37,
        GP_ERROR_IO_SERIAL_SPEED = -41,
        GP_ERROR_IO_USB_CLAIM = -51,
        GP_ERROR_IO_USB_FIND = -52,
        GP_ERROR_IO_LOCK = -60,
        GP_ERROR_HAL = -70,
        GP_ERROR_CORRUPTED_DATA = -102,
        GP_ERROR_FILE_EXISTS = -103,
        GP_ERROR_MODEL_NOT_FOUND = -105,
        GP_ERROR_DIRECTORY_NOT_FOUND = -107,
        GP_ERROR_FILE_NOT_FOUND = -108,
        GP_ERROR_DIRECTORY_EXISTS = -109,
        GP_ERROR_CAMERA_BUSY = -110,
        GP_ERROR_PATH_NOT_ABSOLUTE = -111,
        GP_ERROR_CANCEL = -112,
        GP_ERROR_CAMERA_ERROR = -113,
        GP_ERROR_OS_FAILURE = -114,
        GP_ERROR_NO_SPACE = -115
    }

    // Widget types (subset of libgphoto2 CameraWidgetType)
    internal enum CameraWidgetType
    {
        GP_WIDGET_WINDOW = 0,
        GP_WIDGET_SECTION = 1,
        GP_WIDGET_TEXT = 2,
        GP_WIDGET_RANGE = 3,
        GP_WIDGET_TOGGLE = 4,
        GP_WIDGET_RADIO = 5,
        GP_WIDGET_MENU = 6,
        GP_WIDGET_BUTTON = 7,
        GP_WIDGET_DATE = 8
    }

    internal enum CameraEventType
    {
        GP_EVENT_UNKNOWN = 0,
        GP_EVENT_TIMEOUT = 1,
        GP_EVENT_FILE_ADDED = 2,
        GP_EVENT_FOLDER_ADDED = 3,
        GP_EVENT_CAPTURE_COMPLETE = 4
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gp_context_new();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gp_context_unref(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_new(out IntPtr camera);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_init(IntPtr camera, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_exit(IntPtr camera, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gp_camera_unref(IntPtr camera);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_capture(IntPtr camera, GPCaptureType type, ref CameraFilePath path, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_wait_for_event(IntPtr camera, int timeout, out CameraEventType eventType, out IntPtr eventData, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_file_new(out IntPtr file);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_file_free(IntPtr file);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_file_get(IntPtr camera, string folder, string filename, GPFileType type, IntPtr file, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_file_save(IntPtr file, string filename);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_capture_preview(IntPtr camera, IntPtr file, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_file_get_data_and_size(IntPtr file, out IntPtr data, out ulong size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gp_result_as_string(int result);

    // Config / widget API
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_get_config(IntPtr camera, out IntPtr rootWidget, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_name(IntPtr widget, out IntPtr namePtr);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_count_children(IntPtr widget);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_child(IntPtr widget, int childIndex, out IntPtr childWidget);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_child_by_name(IntPtr widget, string name, out IntPtr childWidget);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_type(IntPtr widget, out CameraWidgetType type);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_value(IntPtr widget, out IntPtr strValue);      // For TEXT/RADIO/MENU
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_value(IntPtr widget, out int intValue);          // For TOGGLE/DATE
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_value(IntPtr widget, out float floatValue);      // For RANGE
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_free(IntPtr widget);


    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_set_config(IntPtr camera, IntPtr rootWidget, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_count_choices(IntPtr widget);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_choice(IntPtr widget, int index, out IntPtr choice);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_set_value(IntPtr widget, string value);          // for TEXT/RADIO/MENU
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_set_value(IntPtr widget, ref int value);         // TOGGLE/DATE
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_set_value(IntPtr widget, ref float value);       // RANGE
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_widget_get_range(IntPtr widget, out float min, out float max, out float step);

    // --- Multi-camera / Port API ---
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_list_new(out IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_list_free(IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_list_count(IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_list_get_name(IntPtr list, int index, out IntPtr name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_list_get_value(IntPtr list, int index, out IntPtr value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_autodetect(IntPtr list, IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_port_info_list_new(out IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_port_info_list_load(IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_port_info_list_lookup_path(IntPtr list, string path);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_port_info_list_get_info(IntPtr list, int index, out IntPtr info);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_port_info_list_free(IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gp_camera_set_port_info(IntPtr camera, IntPtr info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct GPPortInfo
    {
        public int type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string path;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string library;
    }

    private string? _targetPort; // If set, we init this specific camera
    private string? _targetModel;
    private string? _lockedSerial;

    internal sealed record WidgetInfo(
        string Path,
        string Name,
        CameraWidgetType Type,
        string? CurrentValue,
        IReadOnlyList<string>? Choices,
        (float Min, float Max, float Step)? Range
    );

    private static IntPtr ResolvePath(IntPtr root, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IntPtr current = root;
        foreach (var p in parts)
        {
            if (gp_widget_get_child_by_name(current, p, out var next) < 0 || next == IntPtr.Zero)
                return IntPtr.Zero;
            current = next;
        }
        return current;
    }

    private static string ResultString(int code)
        => Marshal.PtrToStringAnsi(gp_result_as_string(code)) ?? $"code {code}";

    private static void ThrowIfError(int code, string where)
    {
        if (code < 0) 
        {
            var errorName = Enum.IsDefined(typeof(GPResult), code) ? ((GPResult)code).ToString() : "Unknown Error";
            Logger.Error($"{where} failed: {ResultString(code)} ({errorName} / {code})");
            throw new InvalidOperationException($"{where} failed: {ResultString(code)} ({errorName} / {code})");
        }
    }

    internal bool IsCameraConnected()
    {
        lock (_sync)
        {
            return _initialized;
        }
    }

    internal void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_sync)
        {
            if (_initialized) return;
            _ctx = gp_context_new();
            if (_ctx == IntPtr.Zero) throw new InvalidOperationException("gp_context_new returned null");
            
            ThrowIfError(gp_camera_new(out _cam), "gp_camera_new");

            string? portToUse = _targetPort;

            if (string.IsNullOrEmpty(portToUse) && !string.IsNullOrEmpty(_targetModel))
            {
                try 
                {
                    var candidates = GetAvailableCameras().Where(c => c.Model == _targetModel).ToList();
                    foreach (var candidate in candidates)
                    {
                        // If we haven't locked a serial yet, just take the first matching model
                        if (string.IsNullOrEmpty(_lockedSerial))
                        {
                            portToUse = candidate.Port;
                            break;
                        }

                        // If we have a locked serial, we must verify it
                        ConfigurePort(_cam, candidate.Port);
                        if (gp_camera_init(_cam, _ctx) >= 0)
                        {
                            _initialized = true; // Temporarily mark as init to use helper
                            var serial = GetWidgetValueByPath("serialnumber") ?? GetWidgetValueByPath("eosserialnumber");
                            _initialized = false; // Reset

                            if (serial == _lockedSerial)
                            {
                                portToUse = candidate.Port;
                                // We are already init'd on the right port, but to be clean and consistent with flow below:
                                gp_camera_exit(_cam, _ctx); 
                                // We will re-init below properly
                                break;
                            }
                            
                            // Wrong serial, cleanup and try next
                            gp_camera_exit(_cam, _ctx);
                        }
                        
                        // Reset camera object for next attempt
                        gp_camera_unref(_cam);
                        gp_camera_new(out _cam);
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(portToUse))
            {
                ConfigurePort(_cam, portToUse);
            }

            try
            {
                ThrowIfError(gp_camera_init(_cam, _ctx), "gp_camera_init");
                _initialized = true;

                // Lock serial if not yet locked
                if (string.IsNullOrEmpty(_lockedSerial))
                {
                    _lockedSerial = GetWidgetValueByPath("serialnumber") ?? GetWidgetValueByPath("eosserialnumber");
                    if (!string.IsNullOrEmpty(_lockedSerial))
                    {
                        Logger.Notice($"[Driver] Locked to camera serial: {_lockedSerial}");
                    }
                }
                
                // Update target port to the one we successfully connected to
                if (!string.IsNullOrEmpty(portToUse)) _targetPort = portToUse;
            }
            catch
            {
                if (_cam != IntPtr.Zero) { gp_camera_unref(_cam); _cam = IntPtr.Zero; }
                if (_ctx != IntPtr.Zero) { gp_context_unref(_ctx); _ctx = IntPtr.Zero; }
                throw;
            }
            _initialized = true;
        }
    }

    // List all connected cameras (Model, Port)
    internal IEnumerable<AvailableCamera> GetAvailableCameras()
    {
        var ctx = gp_context_new();
        IntPtr list = IntPtr.Zero;
        try
        {
            ThrowIfError(gp_list_new(out list), "gp_list_new");
            ThrowIfError(gp_camera_autodetect(list, ctx), "gp_camera_autodetect");
            
            int count = gp_list_count(list);
            for (int i = 0; i < count; i++)
            {
                gp_list_get_name(list, i, out var namePtr);
                gp_list_get_value(list, i, out var valPtr);
                string model = Marshal.PtrToStringAnsi(namePtr) ?? "?";
                string port = Marshal.PtrToStringAnsi(valPtr) ?? "?";
                yield return new AvailableCamera(model, port);
            }
        }
        finally
        {
            if (list != IntPtr.Zero) gp_list_free(list);
            gp_context_unref(ctx);
        }
    }

    private void ConfigurePort(IntPtr camera, string port)
    {
        IntPtr portList = IntPtr.Zero;
        try
        {
            ThrowIfError(gp_port_info_list_new(out portList), "gp_port_info_list_new");
            ThrowIfError(gp_port_info_list_load(portList), "gp_port_info_list_load");
            
            int index = gp_port_info_list_lookup_path(portList, port);
            if (index < 0) throw new InvalidOperationException($"Port '{port}' not found");

            ThrowIfError(gp_port_info_list_get_info(portList, index, out IntPtr infoPtr), "gp_port_info_list_get_info");
            
            // Optional: verify info content by marshalling manually if needed, but passing pointer is safer
            var info = Marshal.PtrToStructure<GPPortInfo>(infoPtr);
            
            ThrowIfError(gp_camera_set_port_info(camera, infoPtr), "gp_camera_set_port_info");
        }
        finally
        {
            if (portList != IntPtr.Zero) gp_port_info_list_free(portList);
        }
    }

    // Select which camera to use for subsequent calls
    internal void SelectCamera(string port, string model)
    {
        lock (_sync)
        {
            if (_initialized) Shutdown(); // Force re-init with new port
            _targetPort = port;
            _targetModel = model;
            _lockedSerial = null; // Reset serial on new selection
        }
    }

    internal void ClearSelectedPort()
    {
        lock (_sync)
        {
            if (_initialized) Shutdown();
            _targetPort = null;
            // Keep _targetModel and _lockedSerial for recovery
        }
    }

    internal void Shutdown()
    {
        lock (_sync)
        {
            if (!_initialized) return;
            try { if (_cam != IntPtr.Zero) gp_camera_exit(_cam, _ctx); } catch { }
            if (_cam != IntPtr.Zero) { gp_camera_unref(_cam); _cam = IntPtr.Zero; }
            if (_ctx != IntPtr.Zero) { gp_context_unref(_ctx); _ctx = IntPtr.Zero; }
            _initialized = false;
        }
    }

    internal void Reinitialize()
    {
        Shutdown();
        EnsureInitialized();
    }

    internal string Capture(string outputPath)
    {
        string filename = "";
        EnsureInitialized();
        lock (_sync)
        {
            var path = new CameraFilePath { name = new string('\0',128), folder = new string('\0',1024) };
            ThrowIfError(gp_camera_capture(_cam, GPCaptureType.Image, ref path, _ctx), "gp_camera_capture");
            ThrowIfError(gp_file_new(out var file), "gp_file_new");
            try
            {
                string extension = System.IO.Path.GetExtension(path.name).TrimStart('.').ToLowerInvariant();
                ThrowIfError(gp_camera_file_get(_cam, path.folder, path.name, GPFileType.Normal, file, _ctx), "gp_camera_file_get");
                ThrowIfError(gp_file_save(file, outputPath + "." + extension), "gp_file_save");
                filename = outputPath + "." + extension;
            }
            finally
            {
                if (file != IntPtr.Zero) gp_file_free(file);
            }
        }
        return filename;
    }

    internal IReadOnlyList<string> ListWidgets()
    {
        EnsureInitialized();
        var list = new List<string>();
        lock (_sync)
        {
            ThrowIfError(gp_camera_get_config(_cam, out var root, _ctx), "gp_camera_get_config");
            try
            {
                void Walk(IntPtr widget, string path)
                {
                    string name = GetWidgetName(widget);
                    string currentPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
                    list.Add(currentPath);
                    int childCount = gp_widget_count_children(widget);
                    if (childCount <= 0) return;
                    for (int i = 0; i < childCount; i++)
                    {
                        if (gp_widget_get_child(widget, i, out var child) < 0 || child == IntPtr.Zero) continue;
                        Walk(child, currentPath);
                    }
                }
                Walk(root, "");
            }
            finally
            {
                if (root != IntPtr.Zero) gp_widget_free(root);
            }
        }
        return list;
    }

    // Get all widget values (path -> value as string)
    internal IDictionary<string,string> GetAllWidgetValues()
    {
        EnsureInitialized();
        var dict = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            ThrowIfError(gp_camera_get_config(_cam, out var root, _ctx), "gp_camera_get_config");
            try
            {
                void Walk(IntPtr widget, string path)
                {
                    string name = GetWidgetName(widget);
                    string currentPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
                    var v = GetWidgetValueSafe(widget);
                    if (v != null) dict[currentPath] = v;
                    int childCount = gp_widget_count_children(widget);
                    if (childCount <= 0) return;
                    for (int i = 0; i < childCount; i++)
                    {
                        if (gp_widget_get_child(widget, i, out var child) < 0 || child == IntPtr.Zero) continue;
                        Walk(child, currentPath);
                    }
                }
                Walk(root, "");
            }
            finally
            {
                if (root != IntPtr.Zero) gp_widget_free(root);
            }
        }
        return dict;
    }

    internal IReadOnlyList<WidgetInfo> GetAllSelectableWidgets()
    {
        EnsureInitialized();
        var list = new List<WidgetInfo>();
        lock (_sync)
        {
            ThrowIfError(gp_camera_get_config(_cam, out var root, _ctx), "gp_camera_get_config");
            try
            {
                void Walk(IntPtr w, string path)
                {
                    string name = GetWidgetName(w);
                    string currentPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
                    if (gp_widget_get_type(w, out var type) >= 0)
                    {
                        if (type is CameraWidgetType.GP_WIDGET_RADIO or CameraWidgetType.GP_WIDGET_MENU or CameraWidgetType.GP_WIDGET_RANGE or CameraWidgetType.GP_WIDGET_TOGGLE or CameraWidgetType.GP_WIDGET_TEXT)
                        {
                            var info = GetWidgetInfo(currentPath);
                            if (info != null) list.Add(info);
                        }
                    }
                    int childCount = gp_widget_count_children(w);
                    if (childCount <= 0) return;
                    for (int i = 0; i < childCount; i++)
                        if (gp_widget_get_child(w, i, out var child) >= 0 && child != IntPtr.Zero)
                            Walk(child, currentPath);
                }
                Walk(root, "");
            }
            finally
            {
                if (root != IntPtr.Zero) gp_widget_free(root);
            }
        }
        return list;
    }

    // Get single widget value by hierarchical path (e.g. "main/imgsettings/iso")
    internal string? GetWidgetValueByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        EnsureInitialized();
        lock (_sync)
        {
            ThrowIfError(gp_camera_get_config(_cam, out var root, _ctx), "gp_camera_get_config");
            try
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                IntPtr current = root;
                foreach (var p in parts)
                {
                    if (gp_widget_get_child_by_name(current, p, out var next) < 0 || next == IntPtr.Zero)
                        return null;
                    current = next;
                }
                return GetWidgetValueSafe(current);
            }
            finally
            {
                if (root != IntPtr.Zero) gp_widget_free(root);
            }
        }
    }

    private static string? GetWidgetValueSafe(IntPtr widget)
    {
        if (widget == IntPtr.Zero) return null;
        if (gp_widget_get_type(widget, out var type) < 0) return null;
        try
        {
            switch (type)
            {
                case CameraWidgetType.GP_WIDGET_TEXT:
                case CameraWidgetType.GP_WIDGET_RADIO:
                case CameraWidgetType.GP_WIDGET_MENU:
                    if (gp_widget_get_value(widget, out IntPtr strPtr) >= 0 && strPtr != IntPtr.Zero)
                        return Marshal.PtrToStringAnsi(strPtr);
                    break;
                case CameraWidgetType.GP_WIDGET_TOGGLE:
                case CameraWidgetType.GP_WIDGET_DATE:
                    if (gp_widget_get_value(widget, out int intVal) >= 0)
                        return intVal.ToString();
                    break;
                case CameraWidgetType.GP_WIDGET_RANGE:
                    if (gp_widget_get_value(widget, out float floatVal) >= 0)
                        return floatVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    return null;
            }
        }
        catch { }
        return null;
    }

    private static string GetWidgetName(IntPtr widget)
    {
        if (widget == IntPtr.Zero) return "<null>";
        return gp_widget_get_name(widget, out var namePtr) >= 0 && namePtr != IntPtr.Zero
            ? (Marshal.PtrToStringAnsi(namePtr) ?? "<noname>")
            : "<noname>";
    }

    internal WidgetInfo? GetWidgetInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        EnsureInitialized();
        lock (_sync)
        {
            ThrowIfError(gp_camera_get_config(_cam, out var root, _ctx), "gp_camera_get_config");
            try
            {
                var widget = ResolvePath(root, path);
                if (widget == IntPtr.Zero) return null;
                var name = GetWidgetName(widget);
                if (gp_widget_get_type(widget, out var type) < 0) return null;
                var value = GetWidgetValueSafe(widget);
                List<string>? choices = null;
                (float Min, float Max, float Step)? range = null;

                switch (type)
                {
                    case CameraWidgetType.GP_WIDGET_RADIO:
                    case CameraWidgetType.GP_WIDGET_MENU:
                        int count = gp_widget_count_choices(widget);
                        if (count > 0)
                        {
                            choices = new List<string>(count);
                            for (int i = 0; i < count; i++)
                            {
                                if (gp_widget_get_choice(widget, i, out var cPtr) >= 0 && cPtr != IntPtr.Zero)
                                {
                                    choices.Add(Marshal.PtrToStringAnsi(cPtr) ?? $"<{i}>");
                                }
                            }
                        }
                        break;
                    case CameraWidgetType.GP_WIDGET_RANGE:
                        if (gp_widget_get_range(widget, out var min, out var max, out var step) >= 0)
                            range = (min, max, step);
                        break;
                }

                return new WidgetInfo(path, name, type, value, choices, range);
            }
            finally
            {
                if (root != IntPtr.Zero) gp_widget_free(root);
            }
        }
    }

    internal bool SetWidgetValueByPath(string path, string value)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        EnsureInitialized();
        lock (_sync)
        {
            ThrowIfError(gp_camera_get_config(_cam, out var root, _ctx), "gp_camera_get_config");
            try
            {
                var widget = ResolvePath(root, path);
                if (widget == IntPtr.Zero) return false;
                if (gp_widget_get_type(widget, out var type) < 0) return false;

                int rc;
                switch (type)
                {
                    case CameraWidgetType.GP_WIDGET_TEXT:
                    case CameraWidgetType.GP_WIDGET_RADIO:
                    case CameraWidgetType.GP_WIDGET_MENU:
                        rc = gp_widget_set_value(widget, value);
                        break;
                    case CameraWidgetType.GP_WIDGET_TOGGLE:
                        int intVal = (value.Equals("1") || value.Equals("true", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                        rc = gp_widget_set_value(widget, ref intVal);
                        break;
                    case CameraWidgetType.GP_WIDGET_RANGE:
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                            return false;
                        rc = gp_widget_set_value(widget, ref f);
                        break;
                    default:
                        return false;
                }
                if (rc < 0) return false;
                // Commit
                if (gp_camera_set_config(_cam, root, _ctx) < 0) return false;
                return true;
            }
            finally
            {
                if (root != IntPtr.Zero) gp_widget_free(root);
            }
        }
    }

    internal bool SetWidgetValueByChoiceIndex(string path, int index)
    {
        var info = GetWidgetInfo(path);
        if (info == null || info.Choices == null) return false;
        if (index < 0 || index >= info.Choices.Count) return false;
        return SetWidgetValueByPath(path, info.Choices[index]);
    }

    // Capture a live preview image and return raw bytes
    internal byte[] CapturePreviewBytes()
    {
        EnsureInitialized();
        lock (_sync)
        {
            ThrowIfError(gp_file_new(out var file), "gp_file_new");
            try
            {
                ThrowIfError(gp_camera_capture_preview(_cam, file, _ctx), "gp_camera_capture_preview");
                ThrowIfError(gp_file_get_data_and_size(file, out var dataPtr, out var size), "gp_file_get_data_and_size");
                if (dataPtr == IntPtr.Zero || size == 0) return Array.Empty<byte>();
                if (size > int.MaxValue) throw new InvalidOperationException("Preview size too large");
                var bytes = new byte[(int)size];
                Marshal.Copy(dataPtr, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                if (file != IntPtr.Zero) gp_file_free(file);
            }
        }
    }

    // Capture a live preview image and save to a file path
    internal void CapturePreviewToFile(string path)
    {
        var buf = CapturePreviewBytes();
        if (buf.Length == 0) throw new InvalidOperationException("No preview data received");
        System.IO.File.WriteAllBytes(path, buf);
    }

    internal string? WaitForImage(int timeoutMs)
    {
        EnsureInitialized();
        lock (_sync)
        {
            IntPtr eventData = IntPtr.Zero;
            try
            {
                ThrowIfError(gp_camera_wait_for_event(_cam, timeoutMs, out var eventType, out eventData, _ctx), "gp_camera_wait_for_event");
                
                if (eventType == CameraEventType.GP_EVENT_FILE_ADDED && eventData != IntPtr.Zero)
                {
                    var path = Marshal.PtrToStructure<CameraFilePath>(eventData);
                    return System.IO.Path.Combine(path.folder, path.name);
                }
                return null;
            }
            finally
            {
                free(eventData);
            }
        }
    }

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void free(IntPtr ptr);

    internal void DownloadFile(string folder, string filename, string outputPath)
    {
        EnsureInitialized();
        lock (_sync)
        {
            ThrowIfError(gp_file_new(out var file), "gp_file_new");
            try
            {
                ThrowIfError(gp_camera_file_get(_cam, folder, filename, GPFileType.Normal, file, _ctx), "gp_camera_file_get");
                ThrowIfError(gp_file_save(file, outputPath), "gp_file_save");
            }
            finally
            {
                if (file != IntPtr.Zero) gp_file_free(file);
            }
        }
    }
}