using System;

namespace Unity.USDToolkit
{
    /// <summary>
    /// A <see cref="UsdStage"/> call OpenUSD refused or could not complete: no prim or attribute
    /// where one was named, a value of another type than the attribute holds, an edit the edit
    /// target cannot take, a file that could not be written.
    /// </summary>
    public sealed class UsdStageException : Exception
    {
        public UsdStageException(string message)
            : base(message)
        {
        }

        public UsdStageException(string message, string diagnostics)
            : base(message)
        {
            Diagnostics = diagnostics;
        }

        /// <summary>OpenUSD's own diagnostics for the call, when they were captured.</summary>
        public string Diagnostics { get; }
    }
}
