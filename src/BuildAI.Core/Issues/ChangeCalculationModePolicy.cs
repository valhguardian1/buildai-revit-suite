using System;

namespace BuildAI.Core.Issues
{
    public enum ChangeCalculationModeSource
    {
        WindowInitialization,
        UserAction,
        OperationReset,
        SettingsLoad
    }

    public sealed class ChangeCalculationModePolicy
    {
        private readonly Action<string> _diagnostic;
        public bool Enabled { get; private set; }

        public ChangeCalculationModePolicy(Action<string> diagnostic = null)
        {
            _diagnostic = diagnostic;
            Set(false, ChangeCalculationModeSource.WindowInitialization);
        }

        public void ApplySettings(bool ignoredSavedValue)
            => Set(false, ChangeCalculationModeSource.SettingsLoad);

        public void SetByUser(bool enabled)
            => Set(enabled, ChangeCalculationModeSource.UserAction);

        public bool ConsumeForOperation()
        {
            var enabled = Enabled;
            Set(false, ChangeCalculationModeSource.OperationReset);
            return enabled;
        }

        public void ResetOperation() => Set(false, ChangeCalculationModeSource.OperationReset);

        private void Set(bool value, ChangeCalculationModeSource source)
        {
            var previous = Enabled;
            Enabled = value;
            var userInitiated = source == ChangeCalculationModeSource.UserAction;
            var reason = source == ChangeCalculationModeSource.WindowInitialization ? "WindowInitialized" :
                source == ChangeCalculationModeSource.OperationReset ? "RunCompleted" : source.ToString();
            _diagnostic?.Invoke("CHANGE_CALCULATION_MODE" + Environment.NewLine +
                                "enabled=" + value.ToString().ToLowerInvariant() + Environment.NewLine +
                                "source=" + source + Environment.NewLine +
                                "CHANGE_CALCULATION_MODE_CHANGED" + Environment.NewLine +
                                "previousValue=" + previous.ToString().ToLowerInvariant() + Environment.NewLine +
                                "newValue=" + value.ToString().ToLowerInvariant() + Environment.NewLine +
                                "reason=" + reason + Environment.NewLine +
                                "userInitiated=" + userInitiated.ToString().ToLowerInvariant());
        }
    }
}
