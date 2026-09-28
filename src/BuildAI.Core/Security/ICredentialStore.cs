namespace BuildAI.Core.Security
{
    /// <summary>
    /// Abstraction over secret storage. The production implementation uses
    /// Windows Credential Manager (specification §9.3 — never store tokens in plaintext).
    /// Swapping this for an OAuth token provider later is a one-line change.
    /// </summary>
    public interface ICredentialStore
    {
        /// <summary>Returns the bearer API token, or null if not set.</summary>
        string GetApiToken();

        /// <summary>Persists the bearer API token securely.</summary>
        void SetApiToken(string token);

        /// <summary>Removes the stored token.</summary>
        void Clear();
    }
}
