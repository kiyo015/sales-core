namespace SalesCore.Application;

// 受注・出荷の API の入力と出力。金額は円、税率は % の数値。状態は英語の名前(Received など)。

/// <summary>受注の登録。単価を省くと商品の標準単価を使う。税率は受注日時点の、商品の税区分の税率。</summary>
public sealed record CreateSalesOrder(string CustomerCode, DateOnly OrderedOn, IReadOnlyList<CreateSalesOrderLine>? Lines);

public sealed record CreateSalesOrderLine(string ProductCode, decimal Quantity, decimal? UnitPrice = null);

/// <summary>出荷の指示。どの受注明細をいくつ、どの倉庫から出すか。</summary>
public sealed record InstructShipment(string WarehouseCode, IReadOnlyList<ShipmentLineRequest>? Lines);

public sealed record ShipmentLineRequest(long OrderLineId, decimal Quantity);

/// <summary>出荷の確定。出荷日が売上日になる。</summary>
public sealed record ConfirmShipment(DateOnly ShippedOn);

public sealed record SalesOrderView(
    long Id, string Number, string Status, string CustomerCode, DateOnly OrderedOn, decimal Amount, IReadOnlyList<SalesOrderLineView> Lines);

public sealed record SalesOrderLineView(
    long Id, string ProductCode, decimal Quantity, decimal UnitPrice, decimal TaxRate, decimal ShippedQuantity, decimal RemainingQuantity);

/// <summary>出荷。確定した出荷には、計上した売上が付く。</summary>
public sealed record ShipmentView(
    long Id, string Number, string Status, long SalesOrderId, DateOnly? ShippedOn, IReadOnlyList<ShipmentLineRequest> Lines, SalesRecordView? SalesRecord);

public sealed record SalesRecordView(long Id, string Number, DateOnly RecordedOn, decimal Amount);

/// <summary>指定した受注・出荷が無い。API は 404 にする。</summary>
public sealed class NotFoundException(string message) : Exception(message);
