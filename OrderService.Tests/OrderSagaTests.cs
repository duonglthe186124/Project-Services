using System.ComponentModel.DataAnnotations;
using OrderService.Controllers;
using OrderService.Dtos;
using OrderService.Models;
using OrderService.Services;
using Xunit;
using S = OrderService.Models.OrderStatusConstants;

namespace OrderService.Tests;

/// <summary>Milestone 3: ma trận Saga, chính sách payment callback, phân quyền xem/hủy đơn, internal API key.</summary>
public class OrderSagaTests
{
    // ---------- Transition matrix ----------
    [Theory]
    [InlineData(S.AwaitingPayment, S.PaymentVerified)]
    [InlineData(S.AwaitingPayment, S.Cancelled)]
    [InlineData(S.AwaitingPayment, S.Expired)]
    [InlineData(S.PaymentVerified, S.ReadyForDispatch)]
    [InlineData(S.PaymentVerified, S.LatePaymentDetected)]
    [InlineData(S.Expired, S.LatePaymentDetected)]
    [InlineData(S.Cancelled, S.LatePaymentDetected)]
    public void Transition_Allowed(string from, string to) => Assert.True(SagaTransitions.IsAllowed(from, to));

    [Theory]
    [InlineData(S.Cancelled, S.PaymentVerified)]
    [InlineData(S.Expired, S.ReadyForDispatch)]
    [InlineData(S.ReadyForDispatch, S.Cancelled)]
    [InlineData(S.Completed, S.Cancelled)]
    [InlineData(S.LatePaymentDetected, S.ReadyForDispatch)]
    [InlineData(S.AwaitingPayment, S.ReadyForDispatch)]
    [InlineData(null, S.Cancelled)]
    public void Transition_Rejected(string? from, string to) => Assert.False(SagaTransitions.IsAllowed(from, to));

    // ---------- Payment callback policy ----------
    [Fact]
    public void Callback_Awaiting_CorrectAmount_Verifies() =>
        Assert.Equal(PaymentCallbackDecision.Verify,
            PaymentCallbackPolicy.Decide(S.AwaitingPayment, 1000m, null, "TX1", 1000m, true));

    [Fact]
    public void Callback_AmountMismatch_Rejected() =>
        Assert.Equal(PaymentCallbackDecision.AmountMismatch,
            PaymentCallbackPolicy.Decide(S.AwaitingPayment, 1000m, null, "TX1", 999m, true));

    [Theory]
    [InlineData(S.Expired)]
    [InlineData(S.Cancelled)]
    public void Callback_AfterExpiredOrCancelled_IsLatePayment(string state) =>
        Assert.Equal(PaymentCallbackDecision.LatePayment,
            PaymentCallbackPolicy.Decide(state, 1000m, null, "TX1", 1000m, true));

    [Theory]
    [InlineData(S.PaymentVerified)]
    [InlineData(S.ReadyForDispatch)]
    [InlineData(S.LatePaymentDetected)]
    public void Callback_SameTransaction_IsReplay(string state) =>
        Assert.Equal(PaymentCallbackDecision.IdempotentReplay,
            PaymentCallbackPolicy.Decide(state, 1000m, "TX1", "TX1", 1000m, true));

    [Fact]
    public void Callback_DifferentTransaction_OnPaidOrder_Conflict() =>
        Assert.Equal(PaymentCallbackDecision.Conflict,
            PaymentCallbackPolicy.Decide(S.ReadyForDispatch, 1000m, "TX1", "TX2", 1000m, true));

    [Fact]
    public void Callback_Failed_WhileAwaiting_RecordsFailureOnly() =>
        Assert.Equal(PaymentCallbackDecision.RecordFailure,
            PaymentCallbackPolicy.Decide(S.AwaitingPayment, 1000m, null, "TX1", 1000m, false));

    [Fact]
    public void Callback_Failed_OnExpired_Conflict() =>
        Assert.Equal(PaymentCallbackDecision.Conflict,
            PaymentCallbackPolicy.Decide(S.Expired, 1000m, null, "TX1", 1000m, false));

    // ---------- Access policy ----------
    [Fact]
    public void Owner_CanView_And_Cancel()
    {
        Assert.True(OrderAccessPolicy.CanView("u1", "u1", false, false));
        Assert.True(OrderAccessPolicy.CanCancel("u1", "u1", false));
    }

    [Fact]
    public void OtherCustomer_CannotView_Or_Cancel()
    {
        Assert.False(OrderAccessPolicy.CanView("u1", "u2", false, false));
        Assert.False(OrderAccessPolicy.CanCancel("u1", "u2", false));
        Assert.False(OrderAccessPolicy.CanView("u1", null, false, false));
    }

    [Fact]
    public void Logistics_CanView_ButNotCancel()
    {
        Assert.True(OrderAccessPolicy.CanView("u1", "staff", false, true));
        Assert.False(OrderAccessPolicy.CanCancel("u1", "staff", false));
    }

    [Fact]
    public void Admin_CanView_And_Cancel() =>
        Assert.True(OrderAccessPolicy.CanView("u1", "a", true, false) && OrderAccessPolicy.CanCancel("u1", "a", true));

    // ---------- DTO masking ----------
    [Fact]
    public void Detail_MasksOtp_ForNonOwner_AndSeal_ForCustomer()
    {
        var order = new Order { Id = "507f1f77bcf86cd799439011", UserId = "u1", DeliveryOtpCode = "123456", SecuritySealCode = "SEAL-1" };
        var asOwner = OrderDetailDto.FromDomain(order, null, isOwner: true, isStaff: false);
        Assert.Equal("123456", asOwner.DeliveryOtpCode);
        Assert.Null(asOwner.SecuritySealCode);

        var asStaff = OrderDetailDto.FromDomain(order, null, isOwner: false, isStaff: true);
        Assert.Null(asStaff.DeliveryOtpCode);
        Assert.Equal("SEAL-1", asStaff.SecuritySealCode);
    }

    [Fact]
    public void Detail_UsesSagaState_AsSourceOfTruth()
    {
        var order = new Order { Id = "507f1f77bcf86cd799439011", OrderStatus = S.AwaitingPayment };
        var saga = new OrderSagaState { CurrentState = S.Cancelled, Version = 2 };
        var dto = OrderDetailDto.FromDomain(order, saga, true, false);
        Assert.Equal(S.Cancelled, dto.OrderStatus);
        Assert.Equal(2, dto.SagaVersion);
    }

    // ---------- Validation ----------
    private static List<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, true);
        return results;
    }

    [Fact]
    public void CallbackRequest_Valid_Passes() =>
        Assert.Empty(Validate(new PaymentCallbackRequest("507f1f77bcf86cd799439011", "TX1", 1000m)));

    [Theory]
    [InlineData("bad-id", "TX1", 1000, "Success")]
    [InlineData("507f1f77bcf86cd799439011", "", 1000, "Success")]
    [InlineData("507f1f77bcf86cd799439011", "TX1", 0, "Success")]
    [InlineData("507f1f77bcf86cd799439011", "TX1", 1000, "Hacked")]
    public void CallbackRequest_Invalid_Fails(string orderId, string tx, double amount, string status) =>
        Assert.NotEmpty(Validate(new PaymentCallbackRequest(orderId, tx, (decimal)amount, Status: status)));

    [Fact]
    public void CancelRequest_TooLongReason_Fails() =>
        Assert.NotEmpty(Validate(new CancelOrderRequest(new string('x', 501))));

    // ---------- Internal API key / paging ----------
    [Fact]
    public void InternalApiKey_Validation()
    {
        Assert.True(InternalOrdersController.IsValidApiKey("secret", "secret"));
        Assert.False(InternalOrdersController.IsValidApiKey("secret", "wrong"));
        Assert.False(InternalOrdersController.IsValidApiKey("secret", null));
        Assert.False(InternalOrdersController.IsValidApiKey("secret", ""));
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 1000, 1, 50)]
    [InlineData(3, 20, 3, 20)]
    public void Paging_IsNormalized(int page, int size, int expPage, int expSize) =>
        Assert.Equal((expPage, expSize), OrderQueryService.NormalizePaging(page, size));
}
