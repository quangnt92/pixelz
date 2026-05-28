# Pixelz Checkout Order System

**.NET Core 8 · SQL Server · Serilog · Clean Architecture · CQRS · Outbox Pattern**

---

## Solution Structure

```
Pixelz.sln
├── src/
│   ├── Pixelz.Domain/
│   │   ├── Common/              AggregateRoot, Money, Result, Exceptions
│   │   └── Orders/              Order (aggregate), OrderItem, OrderStatus
│   │       └── Events/          CheckoutSucceededEvent, PaymentFailedEvent
│   ├── Pixelz.Application/
│   │   ├── Common/              LoggingPipelineBehavior (MediatR)
│   │   ├── Interfaces/          IOrderRepository, IPaymentService, IEmailService, ...
│   │   └── Orders/
│   │       ├── Commands/        CheckoutOrderCommand + Handler
│   │       ├── Queries/         SearchOrdersQuery, GetOrderByIdQuery + Handlers
│   │       └── EventHandlers/   CheckoutSucceededEventHandler
│   ├── Pixelz.Infrastructure/
│   │   ├── Persistence/         PixelzDbContext, OrderRepository, UnitOfWork
│   │   │   └── Configurations/  EF entity configs (owned types, indexes)
│   │   ├── ExternalServices/    MockPayment, MockEmail, MockProduction, MockInvoice
│   │   ├── Outbox/              OutboxMessage + OutboxProcessor (BackgroundService)
│   │   └── Logging/             SerilogConfiguration (console + file sinks)
│   └── Pixelz.API/
│       ├── Controllers/         OrdersController
│       ├── Middleware/          GlobalExceptionMiddleware
│       └── Program.cs           DI wiring + Serilog request logging
└── tests/
    ├── Pixelz.Domain.Tests/        Order aggregate — 9 test cases
    ├── Pixelz.Application.Tests/   Command handler — 5 test cases (NSubstitute)
    └── Pixelz.Integration.Tests/   WebApplicationFactory + in-memory DB
```

---

## Quick Start

### Docker Compose

```bash
docker compose up -d
# API:    http://localhost:5000/swagger
```

Log files are written to a named Docker volume (`logs`) and are also accessible inside the container at `/app/logs`.

### Local dev

```bash
# 1. Start SQL Server
docker run -e ACCEPT_EULA=Y -e SA_PASSWORD=YourStrong!Passw0rd \
  -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest

# 2. Run API — migrations run automatically in Development
cd src/Pixelz.API
dotnet run
# Swagger: https://localhost:5001/swagger
```

---

## Serilog Logging

### Log sinks

| Sink    | Format       | When            | Notes                              |
|---------|--------------|-----------------|------------------------------------|
| Console | Template text | Development     | Human-readable with timestamp + level |
| Console | Plain text    | Production      | Minimal, no color                  |
| File    | Compact JSON  | Always (Info+)  | Rolling daily, 30-day retention    |

### Log file location

Configured via `appsettings.json`:

```json
{
  "Serilog": {
    "LogDirectory": "logs"
  }
}
```

Files are named `logs/pixelz-YYYYMMDD.json`. Each line is a self-contained JSON object (CLEF format — readable by Seq, Kibana, etc.).

### Log levels per environment

| Namespace                          | Development | Production |
|------------------------------------|-------------|------------|
| App code (Pixelz.*)                | Debug       | Information |
| Microsoft.*                        | Warning     | Warning    |
| Microsoft.Hosting.Lifetime         | Information | Information |
| EF Core Database.Command (SQL)     | Information | Warning    |

### Sample log entries

Request log (written by `UseSerilogRequestLogging`):
```json
{"@t":"2024-01-15T10:30:05Z","@m":"HTTP POST /api/v1/orders/{id}/checkout responded 200 in 312.5ms",
 "RequestMethod":"POST","RequestPath":"/api/v1/orders/...","StatusCode":200,"Elapsed":312.5,
 "ClientIP":"::1","MachineName":"srv01","ProcessId":1234}
```

Checkout completed:
```json
{"@t":"2024-01-15T10:30:05Z","@l":"Information",
 "@m":"Checkout completed successfully. OrderId=... PspTxn=mock_ch_...",
 "OrderId":"3fa85f64-...","PspTxn":"mock_ch_abc","SourceContext":"Pixelz.Application.Orders.Commands.CheckoutOrderCommandHandler"}
```

Outbox message failed (with retry):
```json
{"@t":"2024-01-15T10:30:10Z","@l":"Error",
 "@m":"OutboxMessage failed. Id=... EventType=CheckoutSucceededEvent Retry=1/5",
 "Id":"...","EventType":"CheckoutSucceededEvent","Retry":1,"Max":5,
 "@x":"System.Net.Http.HttpRequestException: Production service temporarily unavailable..."}
```

---

## API Reference

### Search Orders
```
GET /api/v1/orders?name=campaign&status=0&page=1&pageSize=20
Authorization: Bearer <jwt>
```

### Get Order Detail
```
GET /api/v1/orders/{id}
Authorization: Bearer <jwt>
```

### Checkout Order
```
POST /api/v1/orders/{id}/checkout
Authorization: Bearer <jwt>
Idempotency-Key: <unique-key>

{
  "paymentMethod": { "type": "card", "token": "tok_visa_1234" }
}
```

**Mock PSP test tokens:**

| Token suffix | Result               |
|---|---|
| `0000`        | card_declined → 402  |
| `9999`        | insufficient_funds → 402 |
| anything else | success → 200        |

---

## Running Tests

```bash
dotnet test                                    # all tests
dotnet test tests/Pixelz.Domain.Tests          # domain only
dotnet test tests/Pixelz.Application.Tests     # application only
dotnet test --collect:"XPlat Code Coverage"    # with coverage
```
