namespace Pixelz.Domain.Common;

public class DomainException(string message) : Exception(message)
{
}

public class NotFoundException(string entityName, object id) : Exception($"{entityName} with id '{id}' was not found.")
{
}

public class BusinessRuleException(string message) : Exception(message)
{
}

public class ExternalServiceException(string serviceName, string message, Exception? inner = null) : Exception(message, inner)
{
    public string ServiceName { get; } = serviceName;
}
