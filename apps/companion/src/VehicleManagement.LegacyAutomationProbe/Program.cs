using System.Runtime.InteropServices;

namespace VehicleManagement.LegacyAutomationProbe;

internal static class Program
{
    private const string ProgId = "FMPRO.Application";

    private static int Main(string[] args)
    {
        if (!args.Contains("--probe", StringComparer.Ordinal))
        {
            return 2;
        }

        var serverType = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (serverType is null)
        {
            return 10;
        }

        if (CLSIDFromProgID(ProgId, out var classId) != 0)
        {
            return 11;
        }

        object? activeObject = null;
        try
        {
            var hresult = GetActiveObject(ref classId, IntPtr.Zero, out activeObject);
            if (hresult != 0)
            {
                return 12;
            }

            return activeObject is not null && Marshal.IsComObject(activeObject) ? 0 : 13;
        }
        finally
        {
            if (activeObject is not null && Marshal.IsComObject(activeObject))
            {
                Marshal.ReleaseComObject(activeObject);
            }
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid classId);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object activeObject);
}
