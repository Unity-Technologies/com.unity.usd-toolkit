using System;

namespace Unity.USDToolkit
{
    public sealed class UsdExportException : Exception
    {
        public UsdExportException(string message)
            : base(message)
        {
        }

        public UsdExportException(string message, string diagnostics)
            : base(message)
        {
            Diagnostics = diagnostics;
        }

        public UsdExportException(string message, Exception innerException, string diagnostics = null)
            : base(message, innerException)
        {
            Diagnostics = diagnostics;
        }

        public string Diagnostics { get; }
    }
}
