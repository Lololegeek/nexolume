namespace OfficeBridge.Models;

public sealed class OfficeException(string code, string message) : Exception(message) { public string Code { get; } = code; }
