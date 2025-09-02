namespace Farm.Web.Client;

// Top-level toast item to avoid nested public type warning (CA1034)
public record ToastItem(Guid Id, string Message, string Type);
