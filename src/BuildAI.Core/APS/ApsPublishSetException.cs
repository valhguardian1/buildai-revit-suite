using System;

namespace BuildAI.Core.APS
{
    /// <summary>
    /// Raised when a required BuildAI 3D view is not part of the Revit publish
    /// set and therefore cannot appear in Autodesk metadata.
    /// <para>
    /// This is a user-actionable configuration problem, not an APS fault and not
    /// a transient condition: retrying without editing Publish Settings will
    /// always fail again. The UI layer catches this type to present a corrective
    /// instruction instead of a generic error dialog.
    /// </para>
    /// </summary>
    public sealed class ApsPublishSetException : InvalidOperationException
    {
        public ApsPublishSetException(string message) : base(message) { }
        public ApsPublishSetException(string message, Exception inner) : base(message, inner) { }
    }
}
