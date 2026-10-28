using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Địa chỉ nhận hàng của khách hàng kèm tọa độ địa lý phục vụ định tuyến 3PL.
/// </summary>
public class ShippingAddress
{
    [BsonElement("addressLine")]
    public string AddressLine { get; set; } = string.Empty;

    [BsonElement("ward")]
    public string Ward { get; set; } = string.Empty;

    [BsonElement("district")]
    public string District { get; set; } = string.Empty;

    [BsonElement("city")]
    public string City { get; set; } = string.Empty;

    [BsonElement("latitude")]
    public double? Latitude { get; set; }

    [BsonElement("longitude")]
    public double? Longitude { get; set; }
}
