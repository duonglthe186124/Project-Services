using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using OrderService.Dtos;
using OrderService.Models;
using OrderService.Services;
using Xunit;

namespace OrderService.Tests;

/// <summary>Unit test Milestone 4: mã đơn/vận đơn, validation checkout, email xác nhận, callback PaymentMethod.</summary>
public class CheckoutAndEmailTests
{
    // ---------- Generators ----------

    [Fact]
    public void OrderCode_HasExpectedFormat_AndIsRandom()
    {
        var date = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        var codes = Enumerable.Range(0, 200).Select(_ => OrderCodeGenerator.Generate(date)).ToList();
        Assert.All(codes, c => Assert.Matches(@"^ADI-20261025-[A-HJ-NP-Z2-9]{6}$", c));
        Assert.True(codes.Distinct().Count() > 190);
    }

    [Fact]
    public void WaybillCode_HasExpectedFormat()
    {
        var code = WaybillCodeGenerator.Generate(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Matches(@"^WB-20260102-[A-HJ-NP-Z2-9]{6}$", code);
    }

    // ---------- MergeLines ----------

    [Fact]
    public void MergeLines_SumsDuplicateProductSize()
    {
        var (lines, error) = OrderCheckoutService.MergeLines(new[]
        {
            ("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 2), ("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 3), ("aaaaaaaaaaaaaaaaaaaaaaaa", 43, 1)
        });
        Assert.Null(error);
        Assert.Equal(2, lines.Count);
        Assert.Equal(5, lines.Single(l => l.Size == 42).Quantity);
    }

    [Fact]
    public void MergeLines_RejectsOverMaxQuantity()
    {
        var (lines, error) = OrderCheckoutService.MergeLines(new[]
        {
            ("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 60), ("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 60)
        });
        Assert.NotNull(error);
        Assert.Empty(lines);
    }

    [Fact]
    public void MergeLines_EmptySource_ReturnsEmpty()
    {
        var (lines, error) = OrderCheckoutService.MergeLines(Array.Empty<(string, int, int)>());
        Assert.Null(error);
        Assert.Empty(lines);
    }

    // ---------- Email resolve ----------

    [Theory]
    [InlineData("body@x.com", "token@x.com", "body@x.com")]
    [InlineData(null, "token@x.com", "token@x.com")]
    [InlineData("  ", "token@x.com", "token@x.com")]
    [InlineData(null, null, null)]
    [InlineData("not-an-email", null, null)]
    [InlineData("a@x.com\r\nBcc: evil@x.com", null, null)]
    [InlineData("a@x.com,b@x.com", null, null)]
    public void ResolveEmail_PrefersBody_AndRejectsInvalid(string? body, string? token, string? expected) =>
        Assert.Equal(expected, OrderCheckoutService.ResolveEmail(body, token));

    // ---------- Request validation ----------

    private static CheckoutAddressRequest ValidAddress() => new("12 Nguyen Hue", "Ben Nghe", "Quan 1", "TP HCM");

    private static List<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, true);
        return results;
    }

    [Theory]
    [InlineData("0912345678", true)]
    [InlineData("+84912345678", true)]
    [InlineData("84912345678", true)]
    [InlineData("0212345678", false)]
    [InlineData("091234567", false)]
    [InlineData("09123456789", false)]
    [InlineData("abc", false)]
    [InlineData("0912345678; DROP", false)]
    public void Phone_Validation(string phone, bool valid)
    {
        var errors = Validate(new CheckoutRequest(phone, ValidAddress()));
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Address_RequiredFields_AndBounds()
    {
        Assert.Empty(Validate(ValidAddress()));
        Assert.NotEmpty(Validate(new CheckoutAddressRequest("", "w", "d", "c")));
        Assert.NotEmpty(Validate(new CheckoutAddressRequest("abc", "w", "d", "c"))); // < 5 ký tự
        Assert.NotEmpty(Validate(new CheckoutAddressRequest(new string('x', 201), "w", "d", "c")));
        Assert.NotEmpty(Validate(new CheckoutAddressRequest("12 Nguyen Hue", "w", "d", "c", 91, 0)));
        Assert.NotEmpty(Validate(new CheckoutAddressRequest("12 Nguyen Hue", "w", "d", "c", 0, 181)));
    }

    [Fact]
    public void CheckoutItem_ValidatesObjectIdSizeQuantity()
    {
        Assert.Empty(Validate(new CheckoutItemRequest("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 1)));
        Assert.NotEmpty(Validate(new CheckoutItemRequest("bad", 42, 1)));
        Assert.NotEmpty(Validate(new CheckoutItemRequest("aaaaaaaaaaaaaaaaaaaaaaaa", 29, 1)));
        Assert.NotEmpty(Validate(new CheckoutItemRequest("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 0)));
        Assert.NotEmpty(Validate(new CheckoutItemRequest("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 101)));
    }

    [Fact]
    public void CheckoutRequest_RejectsBadEmail_AndTooManyItems()
    {
        Assert.NotEmpty(Validate(new CheckoutRequest("0912345678", ValidAddress(), "Nguyen Van A", "nope")));
        var items = Enumerable.Range(0, 51).Select(_ => new CheckoutItemRequest("aaaaaaaaaaaaaaaaaaaaaaaa", 42, 1)).ToList();
        Assert.NotEmpty(Validate(new CheckoutRequest("0912345678", ValidAddress(), Items: items)));
    }

    // ---------- Service guard clauses (không chạm DB/Catalog) ----------

    private static IMongoDatabase LazyDb() => new MongoClient("mongodb://localhost:1").GetDatabase("unit-test-never-connected");

    [Fact]
    public async Task Checkout_MissingNameAndNoClaim_ReturnsBadRequest()
    {
        var svc = new OrderCheckoutService(LazyDb(), null!, null!, NullLogger<OrderCheckoutService>.Instance);
        var r = await svc.CheckoutAsync("u1", "a@x.com", null, new CheckoutRequest("0912345678", ValidAddress()));
        Assert.Equal(OrderResultKind.BadRequest, r.Kind);
    }

    [Fact]
    public async Task Checkout_NoValidEmail_ReturnsBadRequest()
    {
        var svc = new OrderCheckoutService(LazyDb(), null!, null!, NullLogger<OrderCheckoutService>.Instance);
        var r = await svc.CheckoutAsync("u1", null, "Nguyen Van A", new CheckoutRequest("0912345678", ValidAddress()));
        Assert.Equal(OrderResultKind.BadRequest, r.Kind);
    }

    [Fact]
    public async Task Callback_InvalidPaymentMethod_ReturnsBadRequest_BeforeAnyIo()
    {
        var orch = new OrderSagaOrchestrator(LazyDb(), null!, NullLogger<OrderSagaOrchestrator>.Instance);
        var r = await orch.HandlePaymentCallbackAsync(new PaymentCallbackRequest(
            "aaaaaaaaaaaaaaaaaaaaaaaa", "TXN1", 100m, PaymentMethod: "Bitcoin"));
        Assert.Equal(OrderResultKind.BadRequest, r.Kind);
    }

    // ---------- Email builder ----------

    private static Order SampleOrder() => new()
    {
        Id = "aaaaaaaaaaaaaaaaaaaaaaaa",
        OrderCode = "ADI-20261025-ABC234",
        WaybillCode = "WB-20261025-XYZ789",
        CustomerName = "Nguyen <b>Van</b> A",
        CustomerPhone = "0912345678",
        CustomerEmail = "a@x.com",
        PaymentMethod = PaymentMethodConstants.COD,
        ShippingAddress = new ShippingAddress { AddressLine = "12 Nguyen Hue", Ward = "Ben Nghe", District = "Quan 1", City = "TP HCM" },
        Items =
        {
            new OrderItem { ProductName = "Ultraboost <script>alert(1)</script>", Size = 42, Quantity = 2, UnitPrice = 3_000_000m, LineTotal = 6_000_000m }
        },
        SubTotal = 6_000_000m,
        TotalAmount = 6_000_000m
    };

    [Fact]
    public void EmailBuilder_ContainsAllKeyFields_AndEncodesHtml()
    {
        var order = SampleOrder();
        var html = OrderEmailBuilder.BuildHtml(order);
        var text = OrderEmailBuilder.BuildPlainText(order);

        foreach (var s in new[] { "ADI-20261025-ABC234", "WB-20261025-XYZ789", "0912345678", "12 Nguyen Hue", "Size", "COD" })
        {
            Assert.Contains(s, html + text);
        }
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>Van</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("6.000.000", html);
    }

    [Fact]
    public void EmailBuilder_Subject_HasNoLineBreaks()
    {
        var order = SampleOrder();
        order.OrderCode = "ADI-1\r\nBcc: evil@x.com";
        Assert.DoesNotContain('\n', OrderEmailBuilder.BuildSubject(order));
        Assert.DoesNotContain('\r', OrderEmailBuilder.BuildSubject(order));
    }

    [Theory]
    [InlineData("COD")]
    [InlineData("Online_QR")]
    [InlineData("")]
    public void EmailBuilder_PaymentLabel_NeverEmpty(string method) =>
        Assert.False(string.IsNullOrWhiteSpace(OrderEmailBuilder.PaymentMethodLabel(method)));

    // ---------- Email service: không bao giờ ném exception ----------

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task EmailService_NoRecipient_ReturnsFalse()
    {
        var svc = new OrderEmailService(new ConfigurationBuilder().Build(), new FakeEnv(), NullLogger<OrderEmailService>.Instance);
        var order = SampleOrder();
        order.CustomerEmail = null;
        Assert.False(await svc.SendOrderConfirmationAsync(order));
    }

    [Fact]
    public async Task EmailService_MissingSmtpConfig_ReturnsFalse_NoThrow()
    {
        var svc = new OrderEmailService(new ConfigurationBuilder().Build(), new FakeEnv(), NullLogger<OrderEmailService>.Instance);
        Assert.False(await svc.SendOrderConfirmationAsync(SampleOrder()));
    }

    [Fact]
    public async Task EmailService_SmtpUnreachable_ReturnsFalse_NoThrow()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EmailSettings:SmtpServer"] = "127.0.0.1",
            ["EmailSettings:SmtpPort"] = "1",
            ["EmailSettings:SenderEmail"] = "shop@x.com",
            ["EmailSettings:AppPassword"] = "pw"
        }).Build();
        var svc = new OrderEmailService(cfg, new FakeEnv(), NullLogger<OrderEmailService>.Instance);
        Assert.False(await svc.SendOrderConfirmationAsync(SampleOrder()));
    }

    [Fact]
    public void AspNetCoreMvc_ObjectModelValidator_DoesNotThrow_OnAllRequestRecords()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var sp = services.BuildServiceProvider();
        var validator = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator>(sp);

        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        object[] models =
        [
            System.Text.Json.JsonSerializer.Deserialize<AddToCartRequest>(
                """{"productId":"507f1f77bcf86cd799439011","size":42,"quantity":1}""", jsonOpts)!,
            System.Text.Json.JsonSerializer.Deserialize<UpdateCartItemRequest>(
                """{"quantity":3}""", jsonOpts)!,
            System.Text.Json.JsonSerializer.Deserialize<CheckoutRequest>(
                """{"customerPhone":"0912345678","shippingAddress":{"addressLine":"123 Nguyen Hue","ward":"Ben Nghe","district":"Quan 1","city":"TP HCM"}}""", jsonOpts)!,
            System.Text.Json.JsonSerializer.Deserialize<CancelOrderRequest>(
                """{"reason":"Test cancel"}""", jsonOpts)!,
            System.Text.Json.JsonSerializer.Deserialize<PaymentCallbackRequest>(
                """{"orderId":"507f1f77bcf86cd799439011","paymentTransactionId":"TXN-1","amount":100000,"status":"Success"}""", jsonOpts)!
        ];

        foreach (var model in models)
        {
            var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
                new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = sp },
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
            validator.Validate(actionContext, null, string.Empty, model);
            Assert.True(actionContext.ModelState.IsValid);
        }
    }
}
