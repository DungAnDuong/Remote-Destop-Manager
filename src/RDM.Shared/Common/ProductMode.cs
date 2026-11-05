namespace RDM.Shared.Common;

/// <summary>
/// Bản phát hành có máy chủ đóng gói sẵn: người dùng chỉ thấy chức năng, giao diện không nhắc tới máy chủ trung gian,
/// tên miền hay cấu hình của nó. Đặt một lần lúc khởi động. Đây là cách trình bày, không phải biện pháp bảo mật.
/// </summary>
public static class ProductMode
{
    public static bool Managed { get; set; }

    /// <summary>Tên hiển thị của đường truyền qua máy chủ trung gian, dùng ở thanh trạng thái, lịch sử và Audit Log.</summary>
    public static string RelayRouteName => Managed ? "Internet" : "Relay";
}
