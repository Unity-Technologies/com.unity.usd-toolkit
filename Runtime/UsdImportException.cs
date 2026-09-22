using System;

namespace Unity.USDToolkit
{
    public sealed class UsdImportException : Exception
    {
        public UsdImportException(string message)
            : base(message)
        {
        }

        public UsdImportException(string message, string diagnostics)
            : base(message)
        {
            Diagnostics = diagnostics;
        }

        public UsdImportException(string message, Exception innerException, string diagnostics = null)
            : base(message, innerException)
        {
            Diagnostics = diagnostics;
        }

        public string Diagnostics { get; }
    }
}
