using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosSystem.Frontend.Data;
using PosSystem.Frontend.Hardware;
using PosSystem.Frontend.Services;
using PosSystem.Shared.Barcodes;
using PosSystem.Shared.Models;
using PosSystem.Shared.Security;

namespace PosSystem.Frontend.ViewModels;

public class RefundLineSelection : ViewModelBase
{
    private readonly Action _onChanged;
    private int _returnQuantity;

    public RefundLineSelection(SaleLineItem line, Action onChanged)
    {
        LineItemId = line.Id;
        ProductId = line.ProductId;
        ProductName = line.ProductName;
        UnitPrice = line.UnitPrice;
        SoldQuantity = line.Quantity;
        AlreadyReturnedQuantity = line.ReturnedQuantity;
        MaxReturnableQuantity = line.RemainingReturnableQuantity;
        _onChanged = onChanged;
        _returnQuantity = MaxReturnableQuantity;
    }

    public Ulid LineItemId { get; }
    public Ulid ProductId { get; }
    public string ProductName { get; }
    public decimal UnitPrice { get; }
    public int SoldQuantity { get; }
    public int AlreadyReturnedQuantity { get; }
    public int MaxReturnableQuantity { get; }

    public int ReturnQuantity
    {
        get => _returnQuantity;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxReturnableQuantity);
            if (SetProperty(ref _returnQuantity, clamped))
            {
                _onChanged();
            }
        }
    }

    public string SoldSummaryText => AlreadyReturnedQuantity > 0
        ? $"Sold: {SoldQuantity}x @ {UnitPrice:C} • Already Returned: {AlreadyReturnedQuantity}x (Max: {MaxReturnableQuantity}x)"
        : $"Sold: {SoldQuantity}x @ {UnitPrice:C} (Max: {MaxReturnableQuantity}x)";
}

public class CheckoutViewModel : ViewModelBase, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CartExecutionEngine _cartEngine;
    private readonly BarcodeScannerListener _scannerListener;
    private readonly ReceiptPrinterService _printerService;
    private readonly ISessionService _sessionService;
    private readonly INavigationService _navigationService;
    private readonly AuthenticationService _authService;
    private readonly DatabaseMaintenanceService _maintenanceService;

    private List<Product> _allProducts = new();
    private TaxRate _activeTaxRate = new() { Name = "VAT", Percentage = 0.075m, IsInclusive = false };
    private InventorySellPolicy _inventoryPolicy = InventorySellPolicy.BlockOnZeroStock;
    private Action<Cashier>? _pendingAuthorizedAction;
    private string _pendingActionType = string.Empty;
    private string _pendingActionDescription = string.Empty;
    private string? _pendingActionReferenceId;
    private Sale? _refundTargetSale;

    // Collections for UI Binding
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<Product> FilteredProducts { get; } = new();
    public ObservableCollection<SaleLineItem> CartItems { get; } = new();
    public ObservableCollection<Sale> HeldSales { get; } = new();
    public ObservableCollection<Sale> RecentSales { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<Cashier> StaffMembers { get; } = new();
    public ObservableCollection<InventoryMovement> RecentMovements { get; } = new();
    public ObservableCollection<AuditLog> RecentAuditLogs { get; } = new();
    public ObservableCollection<SyncQueue> FailedSyncEntries { get; } = new();
    public ObservableCollection<RefundLineSelection> RefundLineSelections { get; } = new();

    public ObservableCollection<Product> MockProducts => FilteredProducts;

    // Active Workspace Tab: "Register", "Transactions", "Customers", "Shift", "Admin"
    private string _activeSection = "Register";
    public string ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                OnPropertyChanged(nameof(IsRegisterVisible));
                OnPropertyChanged(nameof(IsTransactionsVisible));
                OnPropertyChanged(nameof(IsCustomersVisible));
                OnPropertyChanged(nameof(IsShiftVisible));
                OnPropertyChanged(nameof(IsAdminVisible));
                OnPropertyChanged(nameof(RegisterNavBackground));
                OnPropertyChanged(nameof(RegisterNavForeground));
                OnPropertyChanged(nameof(RegisterNavBorder));
                OnPropertyChanged(nameof(TransactionsNavBackground));
                OnPropertyChanged(nameof(TransactionsNavForeground));
                OnPropertyChanged(nameof(TransactionsNavBorder));
                OnPropertyChanged(nameof(CustomersNavBackground));
                OnPropertyChanged(nameof(CustomersNavForeground));
                OnPropertyChanged(nameof(CustomersNavBorder));
                OnPropertyChanged(nameof(ShiftNavBackground));
                OnPropertyChanged(nameof(ShiftNavForeground));
                OnPropertyChanged(nameof(ShiftNavBorder));
                OnPropertyChanged(nameof(AdminNavBackground));
                OnPropertyChanged(nameof(AdminNavForeground));
                OnPropertyChanged(nameof(AdminNavBorder));
            }
        }
    }

    public Visibility IsRegisterVisible => ActiveSection == "Register" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsTransactionsVisible => ActiveSection == "Transactions" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsCustomersVisible => ActiveSection == "Customers" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsShiftVisible => ActiveSection == "Shift" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsAdminVisible => ActiveSection == "Admin" ? Visibility.Visible : Visibility.Collapsed;

    // Left Navbar Active Screen Color Indicators
    public string RegisterNavBackground => ActiveSection == "Register" ? "#292112" : "Transparent";
    public string RegisterNavForeground => ActiveSection == "Register" ? "#FBBF24" : "#94A3B8";
    public string RegisterNavBorder => ActiveSection == "Register" ? "#FBBF24" : "Transparent";

    public string TransactionsNavBackground => ActiveSection == "Transactions" ? "#0F2942" : "Transparent";
    public string TransactionsNavForeground => ActiveSection == "Transactions" ? "#38BDF8" : "#94A3B8";
    public string TransactionsNavBorder => ActiveSection == "Transactions" ? "#38BDF8" : "Transparent";

    public string CustomersNavBackground => ActiveSection == "Customers" ? "#0D2E22" : "Transparent";
    public string CustomersNavForeground => ActiveSection == "Customers" ? "#4ADE80" : "#94A3B8";
    public string CustomersNavBorder => ActiveSection == "Customers" ? "#4ADE80" : "Transparent";

    public string ShiftNavBackground => ActiveSection == "Shift" ? "#241638" : "Transparent";
    public string ShiftNavForeground => ActiveSection == "Shift" ? "#C084FC" : "#94A3B8";
    public string ShiftNavBorder => ActiveSection == "Shift" ? "#C084FC" : "Transparent";

    public string AdminNavBackground => ActiveSection == "Admin" ? "#331421" : "Transparent";
    public string AdminNavForeground => ActiveSection == "Admin" ? "#FB7185" : "#94A3B8";
    public string AdminNavBorder => ActiveSection == "Admin" ? "#FB7185" : "Transparent";

    private Sale _currentSale = new();
    public Sale CurrentSale
    {
        get => _currentSale;
        set => SetProperty(ref _currentSale, value);
    }

    private SaleLineItem? _selectedCartItem;
    public SaleLineItem? SelectedCartItem
    {
        get => _selectedCartItem;
        set => SetProperty(ref _selectedCartItem, value);
    }

    private Category? _selectedCategory;
    public Category? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
                ApplyProductFilter();
        }
    }

    private Customer? _selectedCustomer;
    public Customer? SelectedCustomer
    {
        get => _selectedCustomer;
        set
        {
            if (SetProperty(ref _selectedCustomer, value))
            {
                CurrentSale.CustomerId = value?.Id;
                CurrentSale.CustomerName = value?.Name;
                if (value == null && CurrentSale.LoyaltyPointsRedeemed > 0)
                {
                    CurrentSale.LoyaltyPointsRedeemed = 0;
                    _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
                    RefreshCart();
                }
                OnPropertyChanged(nameof(AttachedCustomerText));
            }
        }
    }

    public string AttachedCustomerText => SelectedCustomer != null
        ? $"👤 {SelectedCustomer.Name} ({SelectedCustomer.LoyaltyPoints} pts • Credit: {SelectedCustomer.StoreCreditBalance:C})"
        : "👤 Walk-in Customer";

    public string SubtotalText => CurrentSale.Subtotal.ToString("C");
    public string DiscountText => CurrentSale.DiscountAmount > 0
        ? (CurrentSale.LoyaltyPointsRedeemed > 0
            ? $"-{CurrentSale.DiscountAmount:C} ({CurrentSale.LoyaltyPointsRedeemed} pts)"
            : $"-{CurrentSale.DiscountAmount:C} ({CurrentSale.DiscountPercentage:0.#}%)")
        : 0m.ToString("C");
    public string TaxLabelText => _activeTaxRate.DisplayLabel;
    public string TaxText => CurrentSale.TaxAmount.ToString("C");
    public string TotalText => CurrentSale.TotalAmount.ToString("C");
    public string HeldSalesBadge => HeldSales.Count > 0 ? $"Recall Held ({HeldSales.Count})" : "Hold Order (F4)";

    public InventorySellPolicy ActiveInventoryPolicy
    {
        get => _inventoryPolicy;
        private set
        {
            if (SetProperty(ref _inventoryPolicy, value))
                OnPropertyChanged(nameof(InventoryPolicyButtonText));
        }
    }

    public string InventoryPolicyButtonText => ActiveInventoryPolicy switch
    {
        InventorySellPolicy.BlockOnZeroStock => "Stock Policy: Block Out-of-Stock",
        InventorySellPolicy.WarnAndRequireManagerOverride => "Stock Policy: Manager PIN Override",
        _ => "Stock Policy: Allow Negative Stock"
    };

    private string _manualSkuInput = string.Empty;
    public string ManualSkuInput
    {
        get => _manualSkuInput;
        set
        {
            if (SetProperty(ref _manualSkuInput, value))
                ApplyProductFilter();
        }
    }

    private string _numpadBuffer = string.Empty;
    public string NumpadBuffer
    {
        get => _numpadBuffer;
        set
        {
            if (SetProperty(ref _numpadBuffer, value))
                OnPropertyChanged(nameof(NumpadDisplayText));
        }
    }

    private int _nextScanQuantity = 1;
    public int NextScanQuantity
    {
        get => _nextScanQuantity;
        set
        {
            if (SetProperty(ref _nextScanQuantity, Math.Max(1, value)))
                OnPropertyChanged(nameof(NumpadDisplayText));
        }
    }

    public string NumpadDisplayText => string.IsNullOrEmpty(NumpadBuffer)
        ? $"Mode: Ready (Next Qty: {NextScanQuantity}x)"
        : $"Input: {NumpadBuffer}";

    private string _statusMessage = "Ready to scan items or select from catalog.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string SyncStatusText => OutboxSyncService.Instance?.StatusSummary ?? "Offline Mode • Local SQLite WAL";
    public int DeadLetterSyncCount => FailedSyncEntries.Count > 0 ? FailedSyncEntries.Count : (OutboxSyncService.Instance?.DeadLetterCount ?? 0);
    public Visibility DeadLetterBannerVisibility => DeadLetterSyncCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string DeadLetterBannerText => $"⚠️ {DeadLetterSyncCount} sale(s) exceeded max sync retries and moved to Dead-Letter Queue.";

    public Cashier? CurrentUser => _sessionService.CurrentUser;
    public string TerminalName => _sessionService.TerminalId;

    // Multi-Tender Payment Modal State
    private bool _isPaymentModalOpen;
    public bool IsPaymentModalOpen
    {
        get => _isPaymentModalOpen;
        set
        {
            if (SetProperty(ref _isPaymentModalOpen, value))
                OnPropertyChanged(nameof(PaymentModalVisibility));
        }
    }
    public Visibility PaymentModalVisibility => IsPaymentModalOpen ? Visibility.Visible : Visibility.Collapsed;

    private PaymentMethod _selectedPaymentMethod = PaymentMethod.Cash;
    public PaymentMethod SelectedPaymentMethod
    {
        get => _selectedPaymentMethod;
        set
        {
            if (SetProperty(ref _selectedPaymentMethod, value))
                UpdateTenderDefaultAmounts();
        }
    }

    private decimal _cashTenderInput;
    public decimal CashTenderInput
    {
        get => _cashTenderInput;
        set
        {
            if (SetProperty(ref _cashTenderInput, value))
                RecalculatePreviewChange();
        }
    }

    private decimal _cardTenderInput;
    public decimal CardTenderInput
    {
        get => _cardTenderInput;
        set
        {
            if (SetProperty(ref _cardTenderInput, value))
                RecalculatePreviewChange();
        }
    }

    private decimal _transferTenderInput;
    public decimal TransferTenderInput
    {
        get => _transferTenderInput;
        set
        {
            if (SetProperty(ref _transferTenderInput, value))
                RecalculatePreviewChange();
        }
    }

    private decimal _storeCreditTenderInput;
    public decimal StoreCreditTenderInput
    {
        get => _storeCreditTenderInput;
        set
        {
            if (SetProperty(ref _storeCreditTenderInput, value))
                RecalculatePreviewChange();
        }
    }

    private string _paymentReferenceInput = string.Empty;
    public string PaymentReferenceInput
    {
        get => _paymentReferenceInput;
        set => SetProperty(ref _paymentReferenceInput, value);
    }

    public string PreviewTenderTotalText => (CashTenderInput + CardTenderInput + TransferTenderInput + StoreCreditTenderInput).ToString("C");
    public string PreviewChangeDueText
    {
        get
        {
            decimal nonCashApplied = Math.Min(CurrentSale.TotalAmount, CardTenderInput + TransferTenderInput + StoreCreditTenderInput);
            decimal cashBalanceDue = Math.Max(0m, CurrentSale.TotalAmount - nonCashApplied);
            return Math.Max(0m, CashTenderInput - cashBalanceDue).ToString("C");
        }
    }

    // Partial / Full Return Modal State
    private bool _isRefundModalOpen;
    public bool IsRefundModalOpen
    {
        get => _isRefundModalOpen;
        set
        {
            if (SetProperty(ref _isRefundModalOpen, value))
                OnPropertyChanged(nameof(RefundModalVisibility));
        }
    }
    public Visibility RefundModalVisibility => IsRefundModalOpen ? Visibility.Visible : Visibility.Collapsed;

    public string RefundTargetTitle => _refundTargetSale != null
        ? $"Return / Refund Items — {_refundTargetSale.ReceiptNumber}"
        : "Return / Refund Items";

    private string _refundPreviewTotalText = 0m.ToString("C");
    public string RefundPreviewTotalText
    {
        get => _refundPreviewTotalText;
        private set => SetProperty(ref _refundPreviewTotalText, value);
    }

    private string _refundPreviewSummaryText = string.Empty;
    public string RefundPreviewSummaryText
    {
        get => _refundPreviewSummaryText;
        private set => SetProperty(ref _refundPreviewSummaryText, value);
    }

    // Receipt Preview Modal State
    private bool _isReceiptModalOpen;
    public bool IsReceiptModalOpen
    {
        get => _isReceiptModalOpen;
        set
        {
            if (SetProperty(ref _isReceiptModalOpen, value))
                OnPropertyChanged(nameof(ReceiptModalVisibility));
        }
    }
    public Visibility ReceiptModalVisibility => IsReceiptModalOpen ? Visibility.Visible : Visibility.Collapsed;

    private string _receiptPreviewText = string.Empty;
    public string ReceiptPreviewText
    {
        get => _receiptPreviewText;
        set => SetProperty(ref _receiptPreviewText, value);
    }

    // Manager Override PIN Modal State
    private bool _isManagerModalOpen;
    public bool IsManagerModalOpen
    {
        get => _isManagerModalOpen;
        set
        {
            if (SetProperty(ref _isManagerModalOpen, value))
                OnPropertyChanged(nameof(ManagerModalVisibility));
        }
    }
    public Visibility ManagerModalVisibility => IsManagerModalOpen ? Visibility.Visible : Visibility.Collapsed;

    private string _managerOverridePrompt = "Manager or Admin PIN required to authorize this action.";
    public string ManagerOverridePrompt
    {
        get => _managerOverridePrompt;
        set => SetProperty(ref _managerOverridePrompt, value);
    }

    private string _managerPinInput = string.Empty;
    public string ManagerPinInput
    {
        get => _managerPinInput;
        set => SetProperty(ref _managerPinInput, value);
    }

    public Visibility LockOverlayVisibility => _sessionService.IsStationLocked ? Visibility.Visible : Visibility.Collapsed;

    private string _unlockPinInput = string.Empty;
    public string UnlockPinInput
    {
        get => _unlockPinInput;
        set => SetProperty(ref _unlockPinInput, value);
    }

    // Shift & Till Management State
    private ShiftSession? _activeShift;
    public ShiftSession? ActiveShift
    {
        get => _activeShift;
        set
        {
            if (SetProperty(ref _activeShift, value))
            {
                OnPropertyChanged(nameof(HasOpenShift));
                OnPropertyChanged(nameof(NoOpenShiftBannerVisibility));
                OnPropertyChanged(nameof(ShiftStatusSummary));
                OnPropertyChanged(nameof(ExpectedDrawerCashText));
                OnPropertyChanged(nameof(ActiveShiftNonCashText));
            }
        }
    }

    public bool HasOpenShift => ActiveShift != null && !ActiveShift.IsClosed;
    public Visibility NoOpenShiftBannerVisibility => HasOpenShift ? Visibility.Collapsed : Visibility.Visible;

    private decimal _openingFloatInput = 25000m;
    public decimal OpeningFloatInput
    {
        get => _openingFloatInput;
        set => SetProperty(ref _openingFloatInput, value);
    }

    private decimal _cashDropAmountInput;
    public decimal CashDropAmountInput
    {
        get => _cashDropAmountInput;
        set => SetProperty(ref _cashDropAmountInput, value);
    }

    private decimal _actualCountedCashInput;
    public decimal ActualCountedCashInput
    {
        get => _actualCountedCashInput;
        set => SetProperty(ref _actualCountedCashInput, value);
    }

    public string ShiftStatusSummary => HasOpenShift
        ? $"OPEN SHIFT • Opened {ActiveShift!.OpenedAt.ToLocalTime():HH:mm} by {ActiveShift.CashierName} • Float: {ActiveShift.OpeningFloat:C}"
        : "NO ACTIVE SHIFT • Declare your physical opening cash float below before processing checkout sales.";

    public string ExpectedDrawerCashText => (ActiveShift?.ExpectedCash ?? 0m).ToString("C");
    public string ActiveShiftNonCashText => ((ActiveShift?.CardSalesTotal ?? 0m) + (ActiveShift?.TransferSalesTotal ?? 0m)).ToString("C");

    // Admin Back-Office Form State
    private Product _editableProduct = new() { Unit = "ea", ReorderLevel = 10, IsTaxable = true };
    public Product EditableProduct
    {
        get => _editableProduct;
        set => SetProperty(ref _editableProduct, value);
    }

    private Customer _editableCustomer = new();
    public Customer EditableCustomer
    {
        get => _editableCustomer;
        set => SetProperty(ref _editableCustomer, value);
    }

    private string _newStaffUsername = string.Empty;
    public string NewStaffUsername { get => _newStaffUsername; set => SetProperty(ref _newStaffUsername, value); }

    private string _newStaffName = string.Empty;
    public string NewStaffName { get => _newStaffName; set => SetProperty(ref _newStaffName, value); }

    private string _newStaffPin = string.Empty;
    public string NewStaffPin { get => _newStaffPin; set => SetProperty(ref _newStaffPin, value); }

    private UserRole _newStaffRole = UserRole.Staff;
    public UserRole NewStaffRole { get => _newStaffRole; set => SetProperty(ref _newStaffRole, value); }

    public string TodayRevenueText { get; private set; } = 0m.ToString("C");
    public string TodayProfitText { get; private set; } = 0m.ToString("C");
    public int TodaySalesCount { get; private set; }
    public int LowStockCount => _allProducts.Count(p => p.Stock <= p.ReorderLevel);
    public int ExpiringSoonCount => _allProducts.Count(p => p.IsExpired || p.IsExpiringSoon);

    // Commands
    public ICommand AddProductCommand { get; }
    public ICommand AddManualSkuCommand { get; }
    public ICommand SelectCategoryCommand { get; }
    public ICommand IncreaseQtyCommand { get; }
    public ICommand DecreaseQtyCommand { get; }
    public ICommand VoidItemCommand { get; }
    public ICommand VoidAllCommand { get; }
    public ICommand NewSaleCommand { get; }
    public ICommand HoldOrRecallSaleCommand { get; }
    public ICommand PayCommand { get; }
    public ICommand QuickExactCashCommand { get; }

    public ICommand NumpadDigitCommand { get; }
    public ICommand NumpadClearCommand { get; }
    public ICommand NumpadBackspaceCommand { get; }
    public ICommand ApplyQtyMultiplierCommand { get; }
    public ICommand ApplyDiscountPercentCommand { get; }
    public ICommand ApplyPriceOverrideCommand { get; }

    public ICommand SwitchSectionCommand { get; }
    public ICommand LockStationCommand { get; }
    public ICommand UnlockStationCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand TriggerCloudSyncCommand { get; }
    public ICommand RetryFailedSyncsCommand { get; }
    public ICommand DismissFailedSyncCommand { get; }

    public ICommand SelectPaymentMethodCommand { get; }
    public ICommand QuickCashDenomCommand { get; }
    public ICommand ConfirmPaymentCommand { get; }
    public ICommand ClosePaymentModalCommand { get; }
    public ICommand CloseReceiptModalCommand { get; }
    public ICommand ViewSaleReceiptCommand { get; }
    public ICommand RefundSaleCommand { get; }

    public ICommand IncreaseRefundQtyCommand { get; }
    public ICommand DecreaseRefundQtyCommand { get; }
    public ICommand SelectAllRefundItemsCommand { get; }
    public ICommand ClearRefundItemsCommand { get; }
    public ICommand ConfirmRefundModalCommand { get; }
    public ICommand CloseRefundModalCommand { get; }

    public ICommand ConfirmManagerOverrideCommand { get; }
    public ICommand CancelManagerOverrideCommand { get; }

    public ICommand AttachCustomerCommand { get; }
    public ICommand ClearCustomerCommand { get; }
    public ICommand SaveCustomerCommand { get; }
    public ICommand OpenShiftCommand { get; }
    public ICommand RecordCashDropCommand { get; }
    public ICommand CloseShiftZReportCommand { get; }
    public ICommand EditProductInAdminCommand { get; }
    public ICommand SaveProductCommand { get; }
    public ICommand RestockProductCommand { get; }
    public ICommand CreateStaffCommand { get; }
    public ICommand ToggleTaxInclusiveCommand { get; }
    public ICommand CycleInventoryPolicyCommand { get; }
    public ICommand RedeemLoyaltyPointsCommand { get; }
    public ICommand BackupDatabaseCommand { get; }
    public ICommand ExportCsvReportsCommand { get; }
    public ICommand ImportCsvCatalogCommand { get; }

    public CheckoutViewModel(
        IServiceProvider serviceProvider,
        BarcodeScannerListener scannerListener,
        ReceiptPrinterService printerService,
        ISessionService sessionService,
        INavigationService navigationService,
        AuthenticationService authService)
    {
        _serviceProvider = serviceProvider;
        _scannerListener = scannerListener;
        _printerService = printerService;
        _sessionService = sessionService;
        _navigationService = navigationService;
        _authService = authService;
        _maintenanceService = serviceProvider.GetService<DatabaseMaintenanceService>() ?? new DatabaseMaintenanceService(serviceProvider);
        _cartEngine = new CartExecutionEngine();

        AddProductCommand = new RelayCommand(param => AddProduct(param as Product));
        AddManualSkuCommand = new RelayCommand(AddManualSku);
        SelectCategoryCommand = new RelayCommand(param => SelectedCategory = param as Category);
        IncreaseQtyCommand = new RelayCommand(param => IncreaseQty(param as SaleLineItem));
        DecreaseQtyCommand = new RelayCommand(param => DecreaseQty(param as SaleLineItem));
        VoidItemCommand = new RelayCommand(param => VoidItem(param as SaleLineItem));
        VoidAllCommand = new RelayCommand(VoidAllItems);
        NewSaleCommand = new RelayCommand(StartNewSale);
        HoldOrRecallSaleCommand = new RelayCommand(HoldOrRecallSale);
        PayCommand = new RelayCommand(OpenPaymentModal);
        QuickExactCashCommand = new RelayCommand(async _ => await CompleteExactCashSaleAsync());

        NumpadDigitCommand = new RelayCommand(param => AppendNumpadDigit(param?.ToString()));
        NumpadClearCommand = new RelayCommand(() => { NumpadBuffer = string.Empty; NextScanQuantity = 1; });
        NumpadBackspaceCommand = new RelayCommand(BackspaceNumpad);
        ApplyQtyMultiplierCommand = new RelayCommand(ApplyQuantityFromNumpad);
        ApplyDiscountPercentCommand = new RelayCommand(ApplyDiscountFromNumpad);
        ApplyPriceOverrideCommand = new RelayCommand(ApplyPriceOverrideFromNumpad);

        SwitchSectionCommand = new RelayCommand(param => SwitchSection(param?.ToString() ?? "Register"));
        LockStationCommand = new RelayCommand(() =>
        {
            UnlockPinInput = string.Empty;
            _sessionService.SetStationLocked(true);
            OnPropertyChanged(nameof(LockOverlayVisibility));
        });
        UnlockStationCommand = new RelayCommand(UnlockStation);
        LogoutCommand = new RelayCommand(Logout);
        TriggerCloudSyncCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "Syncing with Cloud Backend...";
            if (OutboxSyncService.Instance != null)
                await OutboxSyncService.Instance.TriggerImmediateSyncAsync();
            LoadDatabaseData();
            RefreshSyncIndicators();
            StatusMessage = OutboxSyncService.Instance?.StatusSummary ?? "Local database refreshed.";
        });
        RetryFailedSyncsCommand = new RelayCommand(async _ =>
        {
            if (OutboxSyncService.Instance != null)
            {
                await OutboxSyncService.Instance.RetryDeadLetterQueueAsync();
                LoadDatabaseData();
                RefreshSyncIndicators();
                StatusMessage = "Reset failed sync queue entries and triggered cloud retry.";
            }
        });
        DismissFailedSyncCommand = new RelayCommand(async param =>
        {
            if (param is SyncQueue sq && OutboxSyncService.Instance != null)
            {
                await OutboxSyncService.Instance.DismissDeadLetterEntryAsync(sq.Id);
                LoadDatabaseData();
                RefreshSyncIndicators();
                StatusMessage = $"Dismissed dead-letter sync entry ({sq.SaleId}).";
            }
        });

        SelectPaymentMethodCommand = new RelayCommand(param =>
        {
            if (param is string str && Enum.TryParse<PaymentMethod>(str, out var pm))
                SelectedPaymentMethod = pm;
        });
        QuickCashDenomCommand = new RelayCommand(param =>
        {
            if (param is string s && decimal.TryParse(s, out var amt))
            {
                SelectedPaymentMethod = PaymentMethod.Cash;
                CashTenderInput = amt == 0m ? CurrentSale.TotalAmount : amt;
            }
        });
        ConfirmPaymentCommand = new RelayCommand(async _ => await FinalizeSalePaymentAsync());
        ClosePaymentModalCommand = new RelayCommand(() => IsPaymentModalOpen = false);
        CloseReceiptModalCommand = new RelayCommand(() => IsReceiptModalOpen = false);
        ViewSaleReceiptCommand = new RelayCommand(param => ViewReceiptForSale(param as Sale));
        RefundSaleCommand = new RelayCommand(param => OpenRefundModal(param as Sale));

        IncreaseRefundQtyCommand = new RelayCommand(param =>
        {
            if (param is RefundLineSelection row)
                row.ReturnQuantity += 1;
        });
        DecreaseRefundQtyCommand = new RelayCommand(param =>
        {
            if (param is RefundLineSelection row)
                row.ReturnQuantity -= 1;
        });
        SelectAllRefundItemsCommand = new RelayCommand(() =>
        {
            foreach (var row in RefundLineSelections)
                row.ReturnQuantity = row.MaxReturnableQuantity;
        });
        ClearRefundItemsCommand = new RelayCommand(() =>
        {
            foreach (var row in RefundLineSelections)
                row.ReturnQuantity = 0;
        });
        ConfirmRefundModalCommand = new RelayCommand(ConfirmRefundSelection);
        CloseRefundModalCommand = new RelayCommand(() =>
        {
            IsRefundModalOpen = false;
            _refundTargetSale = null;
            RefundLineSelections.Clear();
        });

        ConfirmManagerOverrideCommand = new RelayCommand(ConfirmManagerOverride);
        CancelManagerOverrideCommand = new RelayCommand(() =>
        {
            IsManagerModalOpen = false;
            _pendingAuthorizedAction = null;
            _pendingActionType = string.Empty;
            _pendingActionDescription = string.Empty;
            _pendingActionReferenceId = null;
            ManagerPinInput = string.Empty;
        });

        AttachCustomerCommand = new RelayCommand(param =>
        {
            if (param is Customer c)
            {
                SelectedCustomer = c;
                ActiveSection = "Register";
                StatusMessage = $"Attached customer {c.Name} ({c.LoyaltyPoints} loyalty pts) to current order.";
            }
        });
        ClearCustomerCommand = new RelayCommand(() => SelectedCustomer = null);
        SaveCustomerCommand = new RelayCommand(SaveCustomer);
        OpenShiftCommand = new RelayCommand(OpenShift);
        RecordCashDropCommand = new RelayCommand(RecordCashDrop);
        CloseShiftZReportCommand = new RelayCommand(CloseShiftAndPrintZReport);
        EditProductInAdminCommand = new RelayCommand(param =>
        {
            if (param is Product p)
            {
                EditableProduct = new Product
                {
                    Id = p.Id,
                    CategoryId = p.CategoryId,
                    Name = p.Name,
                    Sku = p.Sku,
                    Barcode = p.Barcode,
                    Unit = p.Unit,
                    CostPrice = p.CostPrice,
                    Price = p.Price,
                    WholesaleMinQty = p.WholesaleMinQty,
                    WholesaleUnitPrice = p.WholesaleUnitPrice,
                    ExpiryDate = p.ExpiryDate,
                    Stock = p.Stock,
                    ReorderLevel = p.ReorderLevel,
                    IsTaxable = p.IsTaxable
                };
                StatusMessage = $"Editing product: {p.Name}";
            }
        });
        SaveProductCommand = new RelayCommand(SaveProductFromAdmin);
        RestockProductCommand = new RelayCommand(param => QuickRestockProduct(param as Product, 20));
        CreateStaffCommand = new RelayCommand(CreateStaffFromAdmin);
        ToggleTaxInclusiveCommand = new RelayCommand(ToggleTaxInclusiveMode);
        CycleInventoryPolicyCommand = new RelayCommand(CycleInventoryPolicy);
        RedeemLoyaltyPointsCommand = new RelayCommand(RedeemCustomerLoyaltyPoints);
        BackupDatabaseCommand = new RelayCommand(() => { _ = RunHotBackup(); });
        ExportCsvReportsCommand = new RelayCommand(() => { _ = ExportCsvReports(); });
        ImportCsvCatalogCommand = new RelayCommand(() => { _ = ImportCsvCatalog(); });

        LoadDatabaseData();
        LoadActiveShiftFromDatabase();
        StartNewSale();

        if (!HasOpenShift)
        {
            StatusMessage = "⚠️ No active shift open. Open a till shift with your starting cash float before processing checkout payments.";
        }

        _scannerListener.BarcodeScanned += ScannerListener_BarcodeScanned;
        if (OutboxSyncService.Instance != null)
        {
            OutboxSyncService.Instance.SyncStateChanged += OnSyncStateChanged;
        }
    }

    private void OnSyncStateChanged()
    {
        Application.Current?.Dispatcher.Invoke(RefreshSyncIndicators);
    }

    private void RefreshSyncIndicators()
    {
        OnPropertyChanged(nameof(SyncStatusText));
        OnPropertyChanged(nameof(DeadLetterSyncCount));
        OnPropertyChanged(nameof(DeadLetterBannerVisibility));
        OnPropertyChanged(nameof(DeadLetterBannerText));
    }

    public void LoadDatabaseData()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        _activeTaxRate = db.TaxRates.AsNoTracking().FirstOrDefault(t => t.IsDefault)
                         ?? db.TaxRates.AsNoTracking().FirstOrDefault()
                         ?? new TaxRate { Name = "VAT", Percentage = 0.075m };

        var policySetting = db.StoreSettings.AsNoTracking()
            .FirstOrDefault(s => s.Key == StoreSetting.InventoryPolicyKey);
        if (policySetting != null && Enum.TryParse<InventorySellPolicy>(policySetting.Value, out var parsedPolicy))
        {
            ActiveInventoryPolicy = parsedPolicy;
        }
        else
        {
            ActiveInventoryPolicy = InventorySellPolicy.BlockOnZeroStock;
        }

        var dbCategories = db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToList();
        if (Categories.Count != dbCategories.Count + 1)
        {
            Categories.Clear();
            Categories.Add(new Category { Id = Ulid.Empty, Name = "All Items", ColorHex = "#FBBF24" });
            foreach (var cat in dbCategories)
                Categories.Add(cat);
        }
        _selectedCategory ??= Categories.FirstOrDefault();

        _allProducts = db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToList();
        if (EditableProduct.CategoryId == Ulid.Empty && Categories.Count > 1)
        {
            EditableProduct.CategoryId = Categories[1].Id;
        }

        ReplaceCollectionIfChanged(Customers, db.Customers.AsNoTracking().ToList().OrderByDescending(c => c.TotalSpent).ToList(), (a, b) => a.Id == b.Id && a.UpdatedAt == b.UpdatedAt && a.LoyaltyPoints == b.LoyaltyPoints && a.StoreCreditBalance == b.StoreCreditBalance && a.TotalSpent == b.TotalSpent);
        ReplaceCollectionIfChanged(StaffMembers, db.Cashiers.AsNoTracking().OrderBy(c => c.Username).ToList(), (a, b) => a.Id == b.Id && a.UpdatedAt == b.UpdatedAt);

        var suspended = db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .AsSplitQuery()
            .Where(s => s.Status == SaleStatus.Suspended)
            .OrderBy(s => s.CreatedAt)
            .ToList();
        ReplaceCollectionIfChanged(HeldSales, suspended, (a, b) => a.Id == b.Id && a.TotalAmount == b.TotalAmount);

        var sales = db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .AsSplitQuery()
            .Where(s => s.Status != SaleStatus.Suspended)
            .OrderByDescending(s => s.CreatedAt)
            .Take(60)
            .ToList();
        ReplaceCollectionIfChanged(RecentSales, sales, (a, b) => a.Id == b.Id && a.Status == b.Status && a.RefundedAmount == b.RefundedAmount);

        var movements = db.InventoryMovements.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(50).ToList();
        ReplaceCollectionIfChanged(RecentMovements, movements, (a, b) => a.Id == b.Id);

        var audits = db.AuditLogs.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(50).ToList();
        ReplaceCollectionIfChanged(RecentAuditLogs, audits, (a, b) => a.Id == b.Id);

        var failedSyncs = db.SyncQueues
            .AsNoTracking()
            .Include(sq => sq.Sale)
            .Where(sq => sq.Status == SyncStatus.Failed)
            .OrderByDescending(sq => sq.LastAttemptAt)
            .ToList();
        ReplaceCollectionIfChanged(FailedSyncEntries, failedSyncs, (a, b) => a.Id == b.Id && a.RetryCount == b.RetryCount);

        var todayUtc = DateTime.UtcNow.Date;
        var todayCompleted = sales
            .Where(s => s.CreatedAt >= todayUtc &&
                        (s.Status == SaleStatus.Completed || s.Status == SaleStatus.PartiallyRefunded) &&
                        !s.OriginalSaleId.HasValue)
            .ToList();

        decimal netRevenue = todayCompleted.Sum(s => Math.Max(0m, s.TotalAmount - s.RefundedAmount));
        decimal netCost = todayCompleted
            .SelectMany(s => s.Items)
            .Sum(i => i.CostPrice * Math.Max(0, i.Quantity - i.ReturnedQuantity));

        TodayRevenueText = netRevenue.ToString("C");
        TodayProfitText = Math.Max(0m, netRevenue - netCost).ToString("C");
        TodaySalesCount = todayCompleted.Count;

        OnPropertyChanged(nameof(TodayRevenueText));
        OnPropertyChanged(nameof(TodayProfitText));
        OnPropertyChanged(nameof(TodaySalesCount));
        OnPropertyChanged(nameof(LowStockCount));
        OnPropertyChanged(nameof(ExpiringSoonCount));
        OnPropertyChanged(nameof(TaxLabelText));
        OnPropertyChanged(nameof(HeldSalesBadge));
        RefreshSyncIndicators();

        ApplyProductFilter();
    }

    private void LoadActiveShiftFromDatabase()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var openShift = db.ShiftSessions
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefault(s => !s.IsClosed && s.TerminalId == TerminalName)
            ?? db.ShiftSessions.OrderByDescending(s => s.OpenedAt).FirstOrDefault(s => !s.IsClosed);

        ActiveShift = openShift;
        _sessionService.SetCurrentShift(openShift);
    }

    public bool EnforceOpenShiftForSale()
    {
        if (ActiveShift == null || ActiveShift.IsClosed)
        {
            System.Media.SystemSounds.Exclamation.Play();
            ActiveSection = "Shift";
            StatusMessage = "⚠️ Mandatory Shift Float Policy: Declare your opening cash float and click 'Open Shift' before processing payments.";
            return false;
        }
        return true;
    }

    private static void ReplaceCollectionIfChanged<T>(ObservableCollection<T> target, IList<T> source, Func<T, T, bool> areEqual)
    {
        if (target.Count == source.Count)
        {
            bool identical = true;
            for (int i = 0; i < source.Count; i++)
            {
                if (!areEqual(target[i], source[i]))
                {
                    identical = false;
                    break;
                }
            }
            if (identical)
                return;

            for (int i = 0; i < source.Count; i++)
            {
                if (!areEqual(target[i], source[i]))
                    target[i] = source[i];
            }
            return;
        }

        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private void ApplyProductFilter()
    {
        var query = _allProducts.AsEnumerable();

        if (SelectedCategory != null && SelectedCategory.Id != Ulid.Empty)
        {
            query = query.Where(p => p.CategoryId == SelectedCategory.Id);
        }

        if (!string.IsNullOrWhiteSpace(ManualSkuInput) && !WeightedBarcodeParser.IsCandidateVariableMeasureEan13(ManualSkuInput))
        {
            var term = ManualSkuInput.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Barcode != null && p.Barcode.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var filtered = query.ToList();
        ReplaceCollectionIfChanged(
            FilteredProducts,
            filtered,
            (a, b) => a.Id == b.Id && a.Stock == b.Stock && a.Price == b.Price && a.WholesaleMinQty == b.WholesaleMinQty && a.WholesaleUnitPrice == b.WholesaleUnitPrice && a.ExpiryDate == b.ExpiryDate && a.Name == b.Name && a.Sku == b.Sku);
        SyncProductCartReservations();
    }

    private void ScannerListener_BarcodeScanned(object? sender, string barcode)
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() => ProcessScannedOrTypedCode(barcode, allowFuzzyNameMatch: false));
        }
        else
        {
            ProcessScannedOrTypedCode(barcode, allowFuzzyNameMatch: false);
        }
    }

    public bool ProcessScannedOrTypedCode(string rawCode, bool allowFuzzyNameMatch)
    {
        var code = rawCode.Trim();
        if (string.IsNullOrEmpty(code)) return false;

        // 1. Exact Barcode or SKU match
        var exactProduct = _allProducts.FirstOrDefault(p =>
            string.Equals(p.Barcode, code, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Sku, code, StringComparison.OrdinalIgnoreCase));

        if (exactProduct != null)
        {
            int qty = NextScanQuantity;
            NextScanQuantity = 1;
            AddProductWithQuantity(exactProduct, qty);
            return true;
        }

        // 2. GS1 / EAN-13 Variable-Measure Weight or Price Embedded Label (Deli / Butcher / Produce)
        if (WeightedBarcodeParser.IsCandidateVariableMeasureEan13(code))
        {
            if (WeightedBarcodeParser.TryParse(code, _allProducts, out var weightedMatch, out var barcodeError) && weightedMatch != null)
            {
                AddWeightedMatchToCart(weightedMatch);
                NextScanQuantity = 1;
                return true;
            }

            System.Media.SystemSounds.Hand.Play();
            StatusMessage = $"⚠️ {barcodeError ?? $"Invalid variable-measure barcode '{code}'."}";
            return false;
        }

        // 3. Optional fuzzy product name match when typing in the search bar
        if (allowFuzzyNameMatch)
        {
            var fuzzy = _allProducts.FirstOrDefault(p => p.Name.Contains(code, StringComparison.OrdinalIgnoreCase));
            if (fuzzy != null)
            {
                int qty = NextScanQuantity;
                NextScanQuantity = 1;
                AddProductWithQuantity(fuzzy, qty);
                return true;
            }
        }

        System.Media.SystemSounds.Hand.Play();
        StatusMessage = $"⚠️ Unknown Barcode/SKU '{code}'. Product not found in catalog.";
        return false;
    }

    private void StartNewSale()
    {
        CurrentSale = new Sale
        {
            Id = Ulid.NewUlid(),
            ReceiptNumber = Sale.GenerateReceiptNumber(),
            TerminalId = TerminalName,
            ShiftId = ActiveShift?.Id,
            CashierId = CurrentUser?.Id,
            CashierName = CurrentUser?.Name ?? "Cashier"
        };
        SelectedCustomer = null;
        SelectedCartItem = null;
        NumpadBuffer = string.Empty;
        NextScanQuantity = 1;
        _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
        RefreshCart();
        if (HasOpenShift)
        {
            StatusMessage = $"Started new order {CurrentSale.ReceiptNumber}.";
        }
    }

    private void HoldOrRecallSale()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        if (CurrentSale.Items.Any())
        {
            CurrentSale.Status = SaleStatus.Suspended;
            CurrentSale.CreatedAt = DateTime.UtcNow;

            var existing = db.Sales.Include(s => s.Items).FirstOrDefault(s => s.Id == CurrentSale.Id);
            if (existing != null)
            {
                db.Sales.Remove(existing);
                db.SaveChanges();
            }

            db.Sales.Add(CurrentSale);
            db.SaveChanges();

            LoadDatabaseData();
            StartNewSale();
            StatusMessage = $"Order parked on hold in local database. ({HeldSales.Count} held order(s))";
        }
        else if (HeldSales.Any())
        {
            var lastHeld = HeldSales[^1];
            var dbHeld = db.Sales.Include(s => s.Items).FirstOrDefault(s => s.Id == lastHeld.Id);
            if (dbHeld != null)
            {
                var recalled = new Sale
                {
                    Id = dbHeld.Id,
                    ReceiptNumber = dbHeld.ReceiptNumber,
                    TerminalId = dbHeld.TerminalId,
                    ShiftId = ActiveShift?.Id,
                    CashierId = CurrentUser?.Id,
                    CashierName = CurrentUser?.Name ?? dbHeld.CashierName,
                    CustomerId = dbHeld.CustomerId,
                    CustomerName = dbHeld.CustomerName,
                    DiscountPercentage = dbHeld.DiscountPercentage,
                    DiscountAmount = dbHeld.DiscountAmount,
                    Status = SaleStatus.Completed,
                    Items = dbHeld.Items.Select(i => new SaleLineItem
                    {
                        Id = Ulid.NewUlid(),
                        SaleId = dbHeld.Id,
                        ProductId = i.ProductId,
                        ProductName = i.ProductName,
                        Sku = i.Sku,
                        Quantity = i.Quantity,
                        MeasuredWeightKg = i.MeasuredWeightKg,
                        UnitPrice = i.UnitPrice,
                        CostPrice = i.CostPrice,
                        LineDiscountAmount = i.LineDiscountAmount,
                        IsTaxable = i.IsTaxable
                    }).ToList()
                };

                db.Sales.Remove(dbHeld);
                db.SaveChanges();

                LoadDatabaseData();
                CurrentSale = recalled;
                _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
                RefreshCart();
                StatusMessage = $"Recalled held order {CurrentSale.ReceiptNumber}.";
            }
        }
        else
        {
            StatusMessage = "No items in cart to hold, and no held orders to recall.";
        }
    }

    private void RefreshCart()
    {
        var previousSelectedId = SelectedCartItem?.Id;
        CartItems.Clear();
        foreach (var item in CurrentSale.Items)
            CartItems.Add(item);

        SelectedCartItem = CartItems.FirstOrDefault(i => i.Id == previousSelectedId) ?? CartItems.LastOrDefault();
        SyncProductCartReservations();

        OnPropertyChanged(nameof(SubtotalText));
        OnPropertyChanged(nameof(DiscountText));
        OnPropertyChanged(nameof(TaxLabelText));
        OnPropertyChanged(nameof(TaxText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(HeldSalesBadge));
    }

    private void SyncProductCartReservations()
    {
        var reservedByProduct = CurrentSale.Items
            .GroupBy(i => i.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(i => i.MeasuredWeightKg.HasValue
                    ? Math.Max(1, (int)Math.Ceiling(i.MeasuredWeightKg.Value)) * i.Quantity
                    : i.Quantity));

        foreach (var prod in _allProducts)
        {
            prod.ReservedInCart = reservedByProduct.TryGetValue(prod.Id, out int qty) ? qty : 0;
        }

        foreach (var prod in FilteredProducts)
        {
            prod.ReservedInCart = reservedByProduct.TryGetValue(prod.Id, out int qty) ? qty : 0;
        }
    }

    private int GetCurrentCartQtyForProduct(Ulid productId, Ulid? excludeLineId = null)
    {
        return CurrentSale.Items
            .Where(i => i.ProductId == productId && (!excludeLineId.HasValue || i.Id != excludeLineId.Value))
            .Sum(i => i.Quantity);
    }

    private void TryAuthorizeStockForCart(Product product, int desiredTotalProductQty, Action<bool> onApproved)
    {
        if (desiredTotalProductQty <= product.Stock)
        {
            onApproved(false);
            return;
        }

        switch (ActiveInventoryPolicy)
        {
            case InventorySellPolicy.BlockOnZeroStock:
                System.Media.SystemSounds.Exclamation.Play();
                StatusMessage = product.Stock <= 0
                    ? $"⛔ Blocked by Stock Policy: '{product.Name}' is OUT OF STOCK ({product.Stock} available)."
                    : $"⛔ Blocked by Stock Policy: '{product.Name}' only has {product.Stock} {product.Unit} in stock (requested {desiredTotalProductQty}).";
                break;

            case InventorySellPolicy.WarnAndRequireManagerOverride:
                RequireManagerOrAdmin(
                    "NegativeStockOverride",
                    $"Stock Alert: '{product.Name}' has {product.Stock} {product.Unit} in stock (requesting {desiredTotalProductQty}). Authorize negative inventory sale?",
                    _ =>
                    {
                        onApproved(true);
                        StatusMessage = $"⚠️ Supervisor authorized negative-stock sale for '{product.Name}' (Stock after: {product.Stock - desiredTotalProductQty}).";
                    },
                    product.Sku);
                break;

            case InventorySellPolicy.AllowNegativeStock:
                onApproved(true);
                StatusMessage = $"⚠️ Negative stock warning: '{product.Name}' will drop to {product.Stock - desiredTotalProductQty} {product.Unit}.";
                break;
        }
    }

    private void AddProduct(Product? product)
    {
        if (product == null) return;
        int qty = NextScanQuantity;
        NextScanQuantity = 1;
        NumpadBuffer = string.Empty;
        AddProductWithQuantity(product, qty);
    }

    public void AddProductWithQuantity(Product product, int qty)
    {
        if (qty <= 0) qty = 1;
        int alreadyInCart = GetCurrentCartQtyForProduct(product.Id);
        int desiredTotal = alreadyInCart + qty;

        TryAuthorizeStockForCart(product, desiredTotal, isOverride =>
        {
            _cartEngine.AddItemToCart(CurrentSale, product, qty, _activeTaxRate, isOverride);
            RefreshCart();
            if (!isOverride)
            {
                StatusMessage = $"Added {qty}x {product.Name} ({product.Price:C})";
            }
        });
    }

    private void AddWeightedMatchToCart(WeightedBarcodeMatch match)
    {
        int unitsNeeded = Math.Max(1, (int)Math.Ceiling(match.WeightKg));
        int alreadyInCart = GetCurrentCartQtyForProduct(match.Product.Id);
        int desiredTotal = alreadyInCart + unitsNeeded;

        TryAuthorizeStockForCart(match.Product, desiredTotal, isOverride =>
        {
            _cartEngine.AddWeightedItemToCart(CurrentSale, match, _activeTaxRate, isOverride);
            RefreshCart();
            if (!isOverride)
            {
                StatusMessage = $"⚖️ Added weighed label: {match.DisplayLabel} — {match.CalculatedUnitPrice:C}";
            }
        });
    }

    private void AddManualSku()
    {
        var sku = ManualSkuInput.Trim();
        if (string.IsNullOrEmpty(sku)) return;

        if (ProcessScannedOrTypedCode(sku, allowFuzzyNameMatch: true))
        {
            ManualSkuInput = string.Empty;
        }
    }

    private void IncreaseQty(SaleLineItem? item)
    {
        if (item == null) return;
        var product = _allProducts.FirstOrDefault(p => p.Id == item.ProductId);
        int newLineQty = item.Quantity + 1;

        if (product != null)
        {
            int otherLinesQty = GetCurrentCartQtyForProduct(product.Id, excludeLineId: item.Id);
            TryAuthorizeStockForCart(product, otherLinesQty + newLineQty, isOverride =>
            {
                if (isOverride) item.IsNegativeStockAuthorized = true;
                _cartEngine.UpdateItemQuantity(CurrentSale, item, newLineQty, _activeTaxRate);
                RefreshCart();
            });
            return;
        }

        _cartEngine.UpdateItemQuantity(CurrentSale, item, newLineQty, _activeTaxRate);
        RefreshCart();
    }

    private void DecreaseQty(SaleLineItem? item)
    {
        if (item == null) return;
        _cartEngine.UpdateItemQuantity(CurrentSale, item, item.Quantity - 1, _activeTaxRate);
        RefreshCart();
    }

    private void VoidItem(SaleLineItem? item)
    {
        if (item == null) return;

        RequireManagerOrAdmin(
            "ItemVoid",
            $"Authorize Void of '{item.ProductName}' ({item.Quantity}x @ {item.UnitPrice:C})",
            _ =>
            {
                CurrentSale.Items.Remove(item);
                _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
                RefreshCart();
                StatusMessage = $"Voided line item: {item.ProductName}.";
            },
            CurrentSale.ReceiptNumber);
    }

    private void VoidAllItems()
    {
        if (!CurrentSale.Items.Any()) return;

        RequireManagerOrAdmin(
            "CartVoid",
            $"Authorize Void of Entire Order {CurrentSale.ReceiptNumber} ({CurrentSale.TotalAmount:C})",
            _ =>
            {
                CurrentSale.Items.Clear();
                CurrentSale.DiscountPercentage = 0m;
                CurrentSale.DiscountAmount = 0m;
                _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
                RefreshCart();
                StatusMessage = "Entire order voided.";
            },
            CurrentSale.ReceiptNumber);
    }

    private void AppendNumpadDigit(string? digit)
    {
        if (string.IsNullOrEmpty(digit)) return;
        if (NumpadBuffer.Length >= 9) return;
        NumpadBuffer += digit;
    }

    private void BackspaceNumpad()
    {
        if (!string.IsNullOrEmpty(NumpadBuffer))
            NumpadBuffer = NumpadBuffer[..^1];
    }

    private void ApplyQuantityFromNumpad()
    {
        if (!int.TryParse(NumpadBuffer, out var qty) || qty <= 0)
        {
            StatusMessage = "Enter a valid quantity on the keypad first, then press Qty.";
            return;
        }

        NumpadBuffer = string.Empty;

        if (SelectedCartItem != null)
        {
            var targetItem = SelectedCartItem;
            var product = _allProducts.FirstOrDefault(p => p.Id == targetItem.ProductId);
            if (product != null)
            {
                int otherLinesQty = GetCurrentCartQtyForProduct(product.Id, excludeLineId: targetItem.Id);
                TryAuthorizeStockForCart(product, otherLinesQty + qty, isOverride =>
                {
                    if (isOverride) targetItem.IsNegativeStockAuthorized = true;
                    _cartEngine.UpdateItemQuantity(CurrentSale, targetItem, qty, _activeTaxRate);
                    RefreshCart();
                    if (!isOverride)
                        StatusMessage = $"Updated '{targetItem.ProductName}' quantity to {qty}x.";
                });
                return;
            }

            _cartEngine.UpdateItemQuantity(CurrentSale, targetItem, qty, _activeTaxRate);
            RefreshCart();
            StatusMessage = $"Updated '{targetItem.ProductName}' quantity to {qty}x.";
        }
        else
        {
            NextScanQuantity = qty;
            StatusMessage = $"Next item scanned/clicked will add {qty}x.";
        }
    }

    private void ApplyDiscountFromNumpad()
    {
        if (!decimal.TryParse(NumpadBuffer, out var pct) || pct < 0 || pct > 100)
        {
            StatusMessage = "Enter a discount percentage (0 - 100) on the keypad first, then press Disc %.";
            return;
        }

        NumpadBuffer = string.Empty;
        if (pct > 10m)
        {
            RequireManagerOrAdmin(
                "HighDiscountOverride",
                $"Authorize {pct:0.#}% Discount on {CurrentSale.ReceiptNumber} (>10% requires Supervisor)",
                _ =>
                {
                    _cartEngine.ApplyCartDiscountPercentage(CurrentSale, pct, _activeTaxRate);
                    RefreshCart();
                    StatusMessage = $"Applied {pct:0.#}% discount to order.";
                },
                CurrentSale.ReceiptNumber);
        }
        else
        {
            _cartEngine.ApplyCartDiscountPercentage(CurrentSale, pct, _activeTaxRate);
            RefreshCart();
            StatusMessage = $"Applied {pct:0.#}% discount to order.";
        }
    }

    private void ApplyPriceOverrideFromNumpad()
    {
        var target = SelectedCartItem ?? CartItems.LastOrDefault();
        if (target == null)
        {
            StatusMessage = "Select an item in the cart first to override its price.";
            return;
        }

        if (!decimal.TryParse(NumpadBuffer, out var newPrice) || newPrice <= 0m)
        {
            StatusMessage = "Enter the new price on the keypad first, then press Price Ovr.";
            return;
        }

        NumpadBuffer = string.Empty;
        RequireManagerOrAdmin(
            "PriceOverride",
            $"Authorize Price Override on '{target.ProductName}' from {target.UnitPrice:C} to {newPrice:C}",
            _ =>
            {
                _cartEngine.ApplyItemPriceOverride(CurrentSale, target, newPrice, _activeTaxRate);
                RefreshCart();
                StatusMessage = $"Overrode unit price of '{target.ProductName}' to {newPrice:C}.";
            },
            CurrentSale.ReceiptNumber);
    }

    private void OpenPaymentModal()
    {
        if (!EnforceOpenShiftForSale())
            return;

        if (!CurrentSale.Items.Any())
        {
            StatusMessage = "Add at least one item to the cart before checkout.";
            return;
        }

        SelectedPaymentMethod = PaymentMethod.Cash;
        CashTenderInput = CurrentSale.TotalAmount;
        CardTenderInput = 0m;
        TransferTenderInput = 0m;
        StoreCreditTenderInput = 0m;
        PaymentReferenceInput = string.Empty;
        IsPaymentModalOpen = true;
    }

    private void UpdateTenderDefaultAmounts()
    {
        switch (SelectedPaymentMethod)
        {
            case PaymentMethod.Cash:
                CashTenderInput = CurrentSale.TotalAmount;
                CardTenderInput = 0m;
                TransferTenderInput = 0m;
                StoreCreditTenderInput = 0m;
                break;
            case PaymentMethod.Card:
                CashTenderInput = 0m;
                CardTenderInput = CurrentSale.TotalAmount;
                TransferTenderInput = 0m;
                StoreCreditTenderInput = 0m;
                if (string.IsNullOrWhiteSpace(PaymentReferenceInput))
                    PaymentReferenceInput = $"POS-{DateTime.Now:HHmmss}";
                break;
            case PaymentMethod.BankTransfer:
                CashTenderInput = 0m;
                CardTenderInput = 0m;
                TransferTenderInput = CurrentSale.TotalAmount;
                StoreCreditTenderInput = 0m;
                if (string.IsNullOrWhiteSpace(PaymentReferenceInput))
                    PaymentReferenceInput = $"TRF-{DateTime.Now:HHmmss}";
                break;
            case PaymentMethod.StoreCredit:
                decimal maxCredit = SelectedCustomer != null
                    ? Math.Min(CurrentSale.TotalAmount, SelectedCustomer.StoreCreditBalance)
                    : 0m;
                StoreCreditTenderInput = maxCredit;
                CashTenderInput = Math.Max(0m, CurrentSale.TotalAmount - maxCredit);
                CardTenderInput = 0m;
                TransferTenderInput = 0m;
                if (string.IsNullOrWhiteSpace(PaymentReferenceInput))
                    PaymentReferenceInput = $"CRD-{DateTime.Now:HHmmss}";
                break;
            case PaymentMethod.Split:
                decimal half = Math.Round(CurrentSale.TotalAmount / 2m, 2);
                CashTenderInput = half;
                CardTenderInput = CurrentSale.TotalAmount - half;
                TransferTenderInput = 0m;
                StoreCreditTenderInput = 0m;
                break;
        }
        RecalculatePreviewChange();
    }

    public void RedeemCustomerLoyaltyPoints()
    {
        if (SelectedCustomer == null || SelectedCustomer.LoyaltyPoints <= 0)
        {
            StatusMessage = "⚠️ Attach a customer with available loyalty points first.";
            return;
        }

        if (!CurrentSale.Items.Any())
        {
            StatusMessage = "⚠️ Add items to the cart before redeeming loyalty points.";
            return;
        }

        int maxPtsBySubtotal = (int)Math.Floor(CurrentSale.Subtotal / CartExecutionEngine.LoyaltyPointValueNaira);
        int ptsToRedeem = Math.Min(SelectedCustomer.LoyaltyPoints, maxPtsBySubtotal);
        if (ptsToRedeem <= 0)
        {
            StatusMessage = "⚠️ Order subtotal is too small for loyalty point redemption.";
            return;
        }

        CurrentSale.LoyaltyPointsRedeemed = ptsToRedeem;
        _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
        RefreshCart();
        if (IsPaymentModalOpen)
        {
            UpdateTenderDefaultAmounts();
        }
        StatusMessage = $"🎁 Redeemed {ptsToRedeem} loyalty points (-{(ptsToRedeem * CartExecutionEngine.LoyaltyPointValueNaira):C}) for {SelectedCustomer.Name}.";
    }

    private void RecalculatePreviewChange()
    {
        OnPropertyChanged(nameof(PreviewTenderTotalText));
        OnPropertyChanged(nameof(PreviewChangeDueText));
    }

    private async Task CompleteExactCashSaleAsync()
    {
        if (!EnforceOpenShiftForSale())
            return;

        if (!CurrentSale.Items.Any())
        {
            StatusMessage = "Cart is empty.";
            return;
        }

        SelectedPaymentMethod = PaymentMethod.Cash;
        CashTenderInput = CurrentSale.TotalAmount;
        CardTenderInput = 0m;
        TransferTenderInput = 0m;
        StoreCreditTenderInput = 0m;
        PaymentReferenceInput = "EXACT-CASH";
        await FinalizeSalePaymentAsync();
    }

    public async Task<bool> FinalizeSalePaymentAsync()
    {
        if (!EnforceOpenShiftForSale()) return false;
        if (!CurrentSale.Items.Any()) return false;

        if (CashTenderInput < 0m || CardTenderInput < 0m || TransferTenderInput < 0m || StoreCreditTenderInput < 0m)
        {
            StatusMessage = "⚠️ Tender amounts cannot be negative.";
            return false;
        }

        if (StoreCreditTenderInput > 0m)
        {
            if (SelectedCustomer == null)
            {
                StatusMessage = "⚠️ Attach a registered customer before tendering with Store Credit.";
                return false;
            }
            if (StoreCreditTenderInput > SelectedCustomer.StoreCreditBalance)
            {
                StatusMessage = $"⚠️ Store Credit tender ({StoreCreditTenderInput:C}) exceeds {SelectedCustomer.Name}'s available credit ({SelectedCustomer.StoreCreditBalance:C}).";
                return false;
            }
        }

        decimal nonCashTender = CardTenderInput + TransferTenderInput + StoreCreditTenderInput;
        if (nonCashTender > CurrentSale.TotalAmount)
        {
            System.Media.SystemSounds.Exclamation.Play();
            StatusMessage = $"⚠️ Card + Transfer + Store Credit amount ({nonCashTender:C}) cannot exceed order total ({CurrentSale.TotalAmount:C}). Over-tendering for change is only permitted on Cash.";
            return false;
        }

        decimal totalTendered = CashTenderInput + nonCashTender;
        if (totalTendered < CurrentSale.TotalAmount)
        {
            StatusMessage = $"⚠️ Insufficient tender! Short by {(CurrentSale.TotalAmount - totalTendered):C}.";
            return false;
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

            // Final pre-commit stock guard
            var requestedByProduct = CurrentSale.Items
                .GroupBy(i => i.ProductId)
                .ToDictionary(g => g.Key, g => new
                {
                    TotalQty = g.Sum(x => x.Quantity),
                    AnyAuthorized = g.Any(x => x.IsNegativeStockAuthorized)
                });

            foreach (var kvp in requestedByProduct)
            {
                var prod = await db.Products.FirstOrDefaultAsync(p => p.Id == kvp.Key);
                if (prod != null && kvp.Value.TotalQty > prod.Stock)
                {
                    if (ActiveInventoryPolicy == InventorySellPolicy.BlockOnZeroStock)
                    {
                        StatusMessage = $"⛔ Checkout blocked: '{prod.Name}' only has {prod.Stock} in stock.";
                        return false;
                    }
                    if (ActiveInventoryPolicy == InventorySellPolicy.WarnAndRequireManagerOverride && !kvp.Value.AnyAuthorized)
                    {
                        StatusMessage = $"⛔ Checkout requires supervisor override for negative stock on '{prod.Name}'.";
                        return false;
                    }
                }
            }

            CurrentSale.PaymentMethod = SelectedPaymentMethod;
            CurrentSale.CashTendered = CashTenderInput;
            CurrentSale.CardTendered = CardTenderInput;
            CurrentSale.TransferTendered = TransferTenderInput;
            CurrentSale.StoreCreditTendered = StoreCreditTenderInput;
            CurrentSale.PaymentReference = PaymentReferenceInput;
            CurrentSale.CreatedAt = DateTime.UtcNow;
            CurrentSale.ShiftId = ActiveShift?.Id;
            CurrentSale.Status = SaleStatus.Completed;
            _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);

            foreach (var item in CurrentSale.Items)
            {
                var prod = await db.Products.FirstOrDefaultAsync(p => p.Id == item.ProductId);
                if (prod != null)
                {
                    int deductUnits = item.MeasuredWeightKg.HasValue
                        ? Math.Max(1, (int)Math.Ceiling(item.MeasuredWeightKg.Value)) * item.Quantity
                        : item.Quantity;

                    prod.Stock -= deductUnits;
                    prod.UpdatedAt = DateTime.UtcNow;

                    db.InventoryMovements.Add(new InventoryMovement
                    {
                        Id = Ulid.NewUlid(),
                        ProductId = prod.Id,
                        ProductName = prod.Name,
                        Type = MovementType.Sale,
                        QuantityDelta = -deductUnits,
                        StockAfter = prod.Stock,
                        ReferenceId = CurrentSale.ReceiptNumber,
                        PerformedBy = CurrentSale.CashierName,
                        Reason = "POS Checkout Sale",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            if (CurrentSale.CustomerId.HasValue)
            {
                var cust = await db.Customers.FirstOrDefaultAsync(c => c.Id == CurrentSale.CustomerId.Value);
                if (cust != null)
                {
                    cust.TotalSpent += CurrentSale.TotalAmount;
                    cust.VisitCount += 1;
                    int redeemedPts = Math.Clamp(CurrentSale.LoyaltyPointsRedeemed, 0, cust.LoyaltyPoints);
                    int earnedPts = (int)Math.Floor(CurrentSale.TotalAmount / 1000m);
                    cust.LoyaltyPoints = Math.Max(0, cust.LoyaltyPoints - redeemedPts) + earnedPts;
                    if (CurrentSale.StoreCreditTendered > 0m)
                    {
                        cust.StoreCreditBalance = Math.Max(0m, cust.StoreCreditBalance - CurrentSale.StoreCreditTendered);
                    }
                    cust.UpdatedAt = DateTime.UtcNow;
                }
            }

            if (ActiveShift != null)
            {
                var shift = await db.ShiftSessions.FirstOrDefaultAsync(s => s.Id == ActiveShift.Id);
                if (shift != null && !shift.IsClosed)
                {
                    decimal netCashRetained = Math.Max(0m, CurrentSale.CashTendered - CurrentSale.ChangeDue);
                    shift.CashSalesTotal += netCashRetained;
                    shift.CardSalesTotal += CurrentSale.CardTendered;
                    shift.TransferSalesTotal += CurrentSale.TransferTendered;
                    shift.TotalTransactions += 1;
                    shift.ExpectedCash = shift.OpeningFloat + shift.CashSalesTotal - shift.RefundsTotal - shift.CashDropsTotal;
                    ActiveShift = shift;
                }
            }

            db.Sales.Add(CurrentSale);
            db.SyncQueues.Add(new SyncQueue
            {
                Id = Ulid.NewUlid(),
                SaleId = CurrentSale.Id,
                EntityType = "Sale",
                Status = SyncStatus.Pending,
                QueuedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        string renderedReceipt = _printerService.PrintReceipt(CurrentSale);
        IsPaymentModalOpen = false;
        ReceiptPreviewText = renderedReceipt;
        IsReceiptModalOpen = true;

        var completedReceiptNo = CurrentSale.ReceiptNumber;
        var changeDue = CurrentSale.ChangeDue;
        LoadDatabaseData();
        StartNewSale();
        StatusMessage = $"✅ Sale {completedReceiptNo} completed! Change Due: {changeDue:C}. Queued for cloud sync.";

        if (OutboxSyncService.Instance != null)
        {
            _ = OutboxSyncService.Instance.TriggerImmediateSyncAsync();
        }

        return true;
    }

    private void ViewReceiptForSale(Sale? sale)
    {
        if (sale == null) return;
        ReceiptPreviewText = _printerService.FormatReceiptText(sale);
        IsReceiptModalOpen = true;
    }

    // Partial & Full Return / Refund Workflow
    public void OpenRefundModal(Sale? originalSale)
    {
        if (originalSale == null) return;

        if (!originalSale.CanRefund)
        {
            StatusMessage = originalSale.OriginalSaleId.HasValue
                ? $"{originalSale.ReceiptNumber} is already a return/refund slip."
                : $"Receipt {originalSale.ReceiptNumber} has already been fully refunded.";
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var dbSale = db.Sales.AsNoTracking().Include(s => s.Items).FirstOrDefault(s => s.Id == originalSale.Id);
        if (dbSale == null || !dbSale.CanRefund)
        {
            StatusMessage = $"Receipt {originalSale.ReceiptNumber} cannot be refunded.";
            return;
        }

        _refundTargetSale = dbSale;
        RefundLineSelections.Clear();
        foreach (var line in dbSale.Items.Where(i => i.RemainingReturnableQuantity > 0))
        {
            RefundLineSelections.Add(new RefundLineSelection(line, RecalculateRefundModalPreview));
        }

        if (!RefundLineSelections.Any())
        {
            StatusMessage = $"All line items on {dbSale.ReceiptNumber} have already been returned.";
            return;
        }

        OnPropertyChanged(nameof(RefundTargetTitle));
        RecalculateRefundModalPreview();
        IsRefundModalOpen = true;
    }

    private void RecalculateRefundModalPreview()
    {
        if (_refundTargetSale == null)
        {
            RefundPreviewTotalText = 0m.ToString("C");
            RefundPreviewSummaryText = string.Empty;
            return;
        }

        var dict = RefundLineSelections.ToDictionary(r => r.LineItemId, r => r.ReturnQuantity);
        var quote = _cartEngine.CalculatePartialRefund(_refundTargetSale, dict);
        int selectedUnits = RefundLineSelections.Sum(r => r.ReturnQuantity);

        RefundPreviewTotalText = quote.RefundTotal.ToString("C");
        RefundPreviewSummaryText = selectedUnits == 0
            ? "Select at least 1 unit to return."
            : (quote.IsFullRemainingReturn
                ? $"Full Return of all {selectedUnits} remaining unit(s) • VAT: {quote.RefundTax:C}"
                : $"Partial Return of {selectedUnits} unit(s) • Pro-rated VAT: {quote.RefundTax:C}");
    }

    private void ConfirmRefundSelection()
    {
        if (_refundTargetSale == null) return;

        var dict = RefundLineSelections.ToDictionary(r => r.LineItemId, r => r.ReturnQuantity);
        var quote = _cartEngine.CalculatePartialRefund(_refundTargetSale, dict);

        if (!quote.ReturnedItems.Any() || quote.RefundTotal <= 0m)
        {
            StatusMessage = "⚠️ Select at least one item quantity (> 0) to refund.";
            return;
        }

        string modeLabel = quote.IsFullRemainingReturn ? "Full Return" : "Partial Return";
        var targetReceiptNo = _refundTargetSale.ReceiptNumber;
        var targetSaleId = _refundTargetSale.Id;

        RequireManagerOrAdmin(
            "SaleRefund",
            $"Authorize {modeLabel} on {targetReceiptNo} for {quote.RefundTotal:C}",
            authorizer => ExecuteApprovedRefund(targetSaleId, dict, authorizer),
            targetReceiptNo);
    }

    public Sale? ExecuteApprovedRefund(Ulid originalSaleId, IReadOnlyDictionary<Ulid, int> lineReturnQuantities, Cashier authorizer)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var dbSale = db.Sales.Include(s => s.Items).FirstOrDefault(s => s.Id == originalSaleId);
        if (dbSale == null || !dbSale.CanRefund)
            return null;

        var quote = _cartEngine.CalculatePartialRefund(dbSale, lineReturnQuantities);
        if (!quote.ReturnedItems.Any())
            return null;

        foreach (var line in dbSale.Items)
        {
            if (lineReturnQuantities.TryGetValue(line.Id, out int reqQty) && reqQty > 0)
            {
                int actualReturn = Math.Clamp(reqQty, 0, line.RemainingReturnableQuantity);
                line.ReturnedQuantity += actualReturn;
            }
        }

        dbSale.RefundedAmount = Math.Round(dbSale.RefundedAmount + quote.RefundTotal, 2, MidpointRounding.AwayFromZero);
        dbSale.Status = dbSale.Items.All(i => i.ReturnedQuantity >= i.Quantity)
            ? SaleStatus.Refunded
            : SaleStatus.PartiallyRefunded;

        var returnSaleId = Ulid.NewUlid();
        var returnReceiptNo = $"RET-{DateTime.UtcNow:yyyyMMdd}-{Ulid.NewUlid().ToString()[^6..]}";

        foreach (var retItem in quote.ReturnedItems)
        {
            retItem.SaleId = returnSaleId;
        }

        var returnSale = new Sale
        {
            Id = returnSaleId,
            ReceiptNumber = returnReceiptNo,
            OriginalSaleId = dbSale.Id,
            TerminalId = TerminalName,
            ShiftId = ActiveShift?.Id,
            CashierId = CurrentUser?.Id ?? authorizer.Id,
            CashierName = CurrentUser?.Name ?? authorizer.Name,
            CustomerId = dbSale.CustomerId,
            CustomerName = dbSale.CustomerName,
            Subtotal = quote.RefundSubtotal,
            DiscountPercentage = dbSale.DiscountPercentage,
            DiscountAmount = quote.RefundDiscount,
            TaxRatePercentage = dbSale.TaxRatePercentage,
            IsTaxInclusive = dbSale.IsTaxInclusive,
            TaxAmount = quote.RefundTax,
            TotalAmount = quote.RefundTotal,
            PaymentMethod = dbSale.PaymentMethod,
            CashTendered = dbSale.PaymentMethod == PaymentMethod.Cash ? quote.RefundTotal : 0m,
            AmountTendered = quote.RefundTotal,
            Status = SaleStatus.Refunded,
            Notes = $"Return against {dbSale.ReceiptNumber} (Auth: {authorizer.Name})",
            CreatedAt = DateTime.UtcNow,
            Items = quote.ReturnedItems
        };

        foreach (var retItem in returnSale.Items)
        {
            var prod = db.Products.FirstOrDefault(p => p.Id == retItem.ProductId);
            if (prod != null)
            {
                int restoreUnits = retItem.MeasuredWeightKg.HasValue
                    ? Math.Max(1, (int)Math.Ceiling(retItem.MeasuredWeightKg.Value)) * retItem.Quantity
                    : retItem.Quantity;

                prod.Stock += restoreUnits;
                prod.UpdatedAt = DateTime.UtcNow;

                db.InventoryMovements.Add(new InventoryMovement
                {
                    Id = Ulid.NewUlid(),
                    ProductId = prod.Id,
                    ProductName = prod.Name,
                    Type = MovementType.Return,
                    QuantityDelta = restoreUnits,
                    StockAfter = prod.Stock,
                    ReferenceId = returnSale.ReceiptNumber,
                    PerformedBy = authorizer.Name,
                    Reason = $"Return from {dbSale.ReceiptNumber}",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        if (dbSale.CustomerId.HasValue)
        {
            var cust = db.Customers.FirstOrDefault(c => c.Id == dbSale.CustomerId.Value);
            if (cust != null)
            {
                cust.TotalSpent = Math.Max(0m, cust.TotalSpent - quote.RefundTotal);
                int ptsDeduct = (int)Math.Floor(quote.RefundTotal / 1000m);
                cust.LoyaltyPoints = Math.Max(0, cust.LoyaltyPoints - ptsDeduct);
                if (dbSale.PaymentMethod == PaymentMethod.StoreCredit || dbSale.StoreCreditTendered > 0m)
                {
                    decimal creditRestore = dbSale.StoreCreditTendered > 0m
                        ? Math.Min(quote.RefundTotal, dbSale.StoreCreditTendered)
                        : quote.RefundTotal;
                    cust.StoreCreditBalance += creditRestore;
                }
                cust.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (ActiveShift != null)
        {
            var shift = db.ShiftSessions.FirstOrDefault(s => s.Id == ActiveShift.Id);
            if (shift != null && !shift.IsClosed)
            {
                shift.RefundsTotal += quote.RefundTotal;
                shift.ExpectedCash = shift.OpeningFloat + shift.CashSalesTotal - shift.RefundsTotal - shift.CashDropsTotal;
                ActiveShift = shift;
            }
        }

        db.Sales.Add(returnSale);
        db.SyncQueues.Add(new SyncQueue
        {
            Id = Ulid.NewUlid(),
            SaleId = returnSale.Id,
            EntityType = "Sale",
            Status = SyncStatus.Pending,
            QueuedAt = DateTime.UtcNow
        });

        db.SaveChanges();

        IsRefundModalOpen = false;
        _refundTargetSale = null;
        RefundLineSelections.Clear();

        ReceiptPreviewText = _printerService.PrintReceipt(returnSale);
        IsReceiptModalOpen = true;
        LoadDatabaseData();
        StatusMessage = $"✅ Processed {(quote.IsFullRemainingReturn ? "full" : "partial")} refund {returnSale.ReceiptNumber} ({quote.RefundTotal:C}) for {dbSale.ReceiptNumber}.";

        if (OutboxSyncService.Instance != null)
        {
            _ = OutboxSyncService.Instance.TriggerImmediateSyncAsync();
        }

        return returnSale;
    }

    private void SwitchSection(string section)
    {
        if (section == "Admin")
        {
            RequireManagerOrAdmin(
                "OpenBackOfficeAdmin",
                "Manager or Admin PIN required to open Back-Office Admin",
                _ =>
                {
                    ActiveSection = "Admin";
                });
            return;
        }

        ActiveSection = section;
    }

    private void RequireManagerOrAdmin(
        string actionType,
        string prompt,
        Action<Cashier> authorizedAction,
        string? referenceId = null)
    {
        if (CurrentUser != null && CurrentUser.CanManageOrVoid)
        {
            RecordAuditLog(actionType, prompt, CurrentUser, CurrentUser, referenceId);
            authorizedAction(CurrentUser);
            return;
        }

        _pendingActionType = actionType;
        _pendingActionDescription = prompt;
        _pendingActionReferenceId = referenceId;
        _pendingAuthorizedAction = authorizedAction;
        ManagerOverridePrompt = prompt;
        ManagerPinInput = string.Empty;
        IsManagerModalOpen = true;
    }

    private void ConfirmManagerOverride()
    {
        var manager = _authService.VerifyManagerOrAdminPin(ManagerPinInput);
        if (manager != null)
        {
            IsManagerModalOpen = false;
            ManagerPinInput = string.Empty;

            var action = _pendingAuthorizedAction;
            var actionType = _pendingActionType;
            var desc = _pendingActionDescription;
            var refId = _pendingActionReferenceId;

            _pendingAuthorizedAction = null;
            _pendingActionType = string.Empty;
            _pendingActionDescription = string.Empty;
            _pendingActionReferenceId = null;

            RecordAuditLog(actionType, desc, CurrentUser, manager, refId);
            action?.Invoke(manager);
        }
        else
        {
            System.Media.SystemSounds.Hand.Play();
            ManagerOverridePrompt = "❌ Invalid Manager/Admin PIN. Try 1234 (Admin) or 2222 (Manager).";
        }
    }

    private void RecordAuditLog(
        string actionType,
        string description,
        Cashier? performedBy,
        Cashier authorizedBy,
        string? referenceId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        db.AuditLogs.Add(new AuditLog
        {
            Id = Ulid.NewUlid(),
            TerminalId = TerminalName,
            ActionType = actionType,
            Description = description,
            PerformedById = performedBy?.Id ?? authorizedBy.Id,
            PerformedByName = performedBy?.Name ?? authorizedBy.Name,
            AuthorizedById = authorizedBy.Id,
            AuthorizedByName = authorizedBy.Name,
            ReferenceId = referenceId,
            IsSynced = false,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    private void UnlockStation()
    {
        if (CurrentUser != null && _authService.AuthenticateOffline(CurrentUser.Username, UnlockPinInput) != null)
        {
            UnlockPinInput = string.Empty;
            _sessionService.SetStationLocked(false);
            OnPropertyChanged(nameof(LockOverlayVisibility));
            StatusMessage = "Terminal unlocked.";
            return;
        }

        var mgr = _authService.VerifyManagerOrAdminPin(UnlockPinInput);
        if (mgr != null)
        {
            UnlockPinInput = string.Empty;
            _sessionService.SetStationLocked(false);
            OnPropertyChanged(nameof(LockOverlayVisibility));
            RecordAuditLog("StationUnlockOverride", $"Terminal unlocked by supervisor {mgr.Name}", CurrentUser, mgr);
            StatusMessage = $"Terminal unlocked by {mgr.Name}.";
            return;
        }

        System.Media.SystemSounds.Hand.Play();
    }

    private void Logout()
    {
        _sessionService.Logout();
        _navigationService.NavigateTo<LoginViewModel>();
    }

    private void SaveCustomer()
    {
        if (string.IsNullOrWhiteSpace(EditableCustomer.Name) || string.IsNullOrWhiteSpace(EditableCustomer.Phone))
        {
            StatusMessage = "Customer Name and Phone are required.";
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var existing = db.Customers.FirstOrDefault(c => c.Phone == EditableCustomer.Phone.Trim());
        if (existing == null)
        {
            var newCust = new Customer
            {
                Id = Ulid.NewUlid(),
                Name = EditableCustomer.Name.Trim(),
                Phone = EditableCustomer.Phone.Trim(),
                Email = EditableCustomer.Email?.Trim(),
                LoyaltyPoints = 50,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Customers.Add(newCust);
            StatusMessage = $"Registered customer '{newCust.Name}' with 50 welcome points.";
        }
        else
        {
            existing.Name = EditableCustomer.Name.Trim();
            existing.Email = EditableCustomer.Email?.Trim();
            existing.UpdatedAt = DateTime.UtcNow;
            StatusMessage = $"Updated customer '{existing.Name}'.";
        }

        db.SaveChanges();
        EditableCustomer = new Customer();
        LoadDatabaseData();
    }

    public void OpenShift()
    {
        if (OpeningFloatInput < 0m)
        {
            StatusMessage = "Opening float cannot be negative.";
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var existingOpen = db.ShiftSessions.FirstOrDefault(s => !s.IsClosed && s.TerminalId == TerminalName)
                           ?? db.ShiftSessions.FirstOrDefault(s => !s.IsClosed);
        if (existingOpen != null)
        {
            StatusMessage = "A shift is already open. Close the current shift before opening a new one.";
            return;
        }

        var newShift = new ShiftSession
        {
            Id = Ulid.NewUlid(),
            TerminalId = TerminalName,
            CashierId = CurrentUser?.Id ?? Ulid.NewUlid(),
            CashierName = CurrentUser?.Name ?? "Cashier",
            OpenedAt = DateTime.UtcNow,
            OpeningFloat = OpeningFloatInput,
            ExpectedCash = OpeningFloatInput,
            IsClosed = false
        };

        db.ShiftSessions.Add(newShift);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ulid.NewUlid(),
            TerminalId = TerminalName,
            ActionType = "ShiftOpen",
            Description = $"Opened shift with starting cash float {newShift.OpeningFloat:C}",
            PerformedById = CurrentUser?.Id,
            PerformedByName = newShift.CashierName,
            AuthorizedById = CurrentUser?.Id,
            AuthorizedByName = newShift.CashierName,
            ReferenceId = newShift.Id.ToString(),
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        ActiveShift = newShift;
        CurrentSale.ShiftId = newShift.Id;
        _sessionService.SetCurrentShift(newShift);
        LoadDatabaseData();
        StatusMessage = $"✅ Opened new shift with float {newShift.OpeningFloat:C}. Register is ready for checkout.";
    }

    private void RecordCashDrop()
    {
        if (ActiveShift == null || ActiveShift.IsClosed || CashDropAmountInput <= 0m)
        {
            StatusMessage = "Enter a valid cash drop amount for an open shift.";
            return;
        }

        RequireManagerOrAdmin(
            "SafeDrop",
            $"Authorize Safe Drop / Payout of {CashDropAmountInput:C}",
            _ =>
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                var shift = db.ShiftSessions.FirstOrDefault(s => s.Id == ActiveShift.Id);
                if (shift != null)
                {
                    shift.CashDropsTotal += CashDropAmountInput;
                    shift.ExpectedCash = shift.OpeningFloat + shift.CashSalesTotal - shift.RefundsTotal - shift.CashDropsTotal;
                    db.SaveChanges();
                    ActiveShift = shift;
                    LoadDatabaseData();
                    StatusMessage = $"Recorded safe drop of {CashDropAmountInput:C}. Expected drawer cash: {shift.ExpectedCash:C}.";
                    CashDropAmountInput = 0m;
                }
            },
            ActiveShift.Id.ToString());
    }

    private void CloseShiftAndPrintZReport()
    {
        if (ActiveShift == null || ActiveShift.IsClosed)
        {
            StatusMessage = "No active open shift to close.";
            return;
        }

        RequireManagerOrAdmin(
            "ShiftCloseZReport",
            $"Authorize End-of-Shift Z-Report & Blind Cash Count ({ActualCountedCashInput:C})",
            _ =>
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                var shift = db.ShiftSessions.FirstOrDefault(s => s.Id == ActiveShift.Id);
                if (shift != null)
                {
                    shift.ActualCountedCash = ActualCountedCashInput;
                    shift.ExpectedCash = shift.OpeningFloat + shift.CashSalesTotal - shift.RefundsTotal - shift.CashDropsTotal;
                    shift.Variance = shift.ActualCountedCash - shift.ExpectedCash;
                    shift.ClosedAt = DateTime.UtcNow;
                    shift.IsClosed = true;
                    db.SaveChanges();

                    string backupNote = string.Empty;
                    try
                    {
                        var backupRes = _maintenanceService.CreateHotBackup("shiftclose");
                        if (backupRes.Success)
                        {
                            backupNote = $" • Backup saved ({Math.Max(1, backupRes.FileSizeBytes / 1024)} KB)";
                        }
                    }
                    catch
                    {
                        // Non-blocking if disk is read-only
                    }

                    ActiveShift = shift;
                    _sessionService.SetCurrentShift(null);
                    ReceiptPreviewText = _printerService.PrintZReport(shift);
                    IsReceiptModalOpen = true;
                    LoadDatabaseData();
                    StatusMessage = $"Shift closed! Z-Report generated. Variance: {(shift.Variance ?? 0m):C}{backupNote}.";
                }
            },
            ActiveShift.Id.ToString());
    }

    public BackupResult RunHotBackup()
    {
        var result = _maintenanceService.CreateHotBackup("manual");
        if (CurrentUser != null && result.Success)
        {
            RecordAuditLog("DatabaseBackup", $"Created SQLite hot backup ({result.BackupFilePath})", CurrentUser, CurrentUser);
        }
        LoadDatabaseData();
        StatusMessage = result.Message;
        return result;
    }

    public CsvExportResult ExportCsvReports()
    {
        var result = _maintenanceService.ExportAllReportsToCsv();
        if (CurrentUser != null && result.Success)
        {
            RecordAuditLog("CsvExport", $"Exported {result.FilesExported} CSV reports ({result.TotalRowsExported} rows)", CurrentUser, CurrentUser);
        }
        LoadDatabaseData();
        StatusMessage = result.Message;
        return result;
    }

    public CsvImportResult ImportCsvCatalog()
    {
        var result = _maintenanceService.ImportProductsFromCsv();
        if (CurrentUser != null && result.Success)
        {
            RecordAuditLog("CsvCatalogImport", $"Imported supplier catalog CSV ({result.CreatedCount} created, {result.UpdatedCount} updated)", CurrentUser, CurrentUser);
        }
        LoadDatabaseData();
        StatusMessage = result.Message;
        return result;
    }

    private void SaveProductFromAdmin()
    {
        if (string.IsNullOrWhiteSpace(EditableProduct.Name) || EditableProduct.Price <= 0m)
        {
            StatusMessage = "Product Name and Selling Price (> 0) are required.";
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var existing = db.Products.FirstOrDefault(p => p.Id == EditableProduct.Id);
        var defaultCatId = Categories.Skip(1).FirstOrDefault()?.Id ?? Ulid.NewUlid();

        if (existing == null)
        {
            var prod = new Product
            {
                Id = Ulid.NewUlid(),
                CategoryId = EditableProduct.CategoryId == Ulid.Empty ? defaultCatId : EditableProduct.CategoryId,
                Name = EditableProduct.Name.Trim(),
                Sku = string.IsNullOrWhiteSpace(EditableProduct.Sku) ? $"SKU-{DateTime.Now:mmss}" : EditableProduct.Sku.Trim(),
                Barcode = string.IsNullOrWhiteSpace(EditableProduct.Barcode) ? EditableProduct.Sku.Trim() : EditableProduct.Barcode.Trim(),
                Unit = string.IsNullOrWhiteSpace(EditableProduct.Unit) ? "ea" : EditableProduct.Unit.Trim(),
                CostPrice = EditableProduct.CostPrice,
                Price = EditableProduct.Price,
                WholesaleMinQty = EditableProduct.WholesaleMinQty,
                WholesaleUnitPrice = EditableProduct.WholesaleUnitPrice,
                ExpiryDate = EditableProduct.ExpiryDate,
                Stock = EditableProduct.Stock,
                ReorderLevel = EditableProduct.ReorderLevel,
                IsTaxable = EditableProduct.IsTaxable,
                IsActive = true,
                UpdatedAt = DateTime.UtcNow
            };
            db.Products.Add(prod);
            StatusMessage = $"Created new product '{prod.Name}' ({prod.Price:C}).";
        }
        else
        {
            existing.Name = EditableProduct.Name.Trim();
            existing.Sku = EditableProduct.Sku.Trim();
            existing.Barcode = EditableProduct.Barcode?.Trim();
            existing.CostPrice = EditableProduct.CostPrice;
            existing.Price = EditableProduct.Price;
            existing.WholesaleMinQty = EditableProduct.WholesaleMinQty;
            existing.WholesaleUnitPrice = EditableProduct.WholesaleUnitPrice;
            existing.ExpiryDate = EditableProduct.ExpiryDate;
            existing.Stock = EditableProduct.Stock;
            existing.ReorderLevel = EditableProduct.ReorderLevel;
            existing.IsTaxable = EditableProduct.IsTaxable;
            existing.UpdatedAt = DateTime.UtcNow;
            StatusMessage = $"Updated product '{existing.Name}'.";
        }

        db.SaveChanges();
        EditableProduct = new Product { Unit = "ea", ReorderLevel = 10, IsTaxable = true, CategoryId = defaultCatId };
        LoadDatabaseData();
    }

    private void QuickRestockProduct(Product? product, int addUnits)
    {
        if (product == null) return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var dbProd = db.Products.FirstOrDefault(p => p.Id == product.Id);
        if (dbProd != null)
        {
            dbProd.Stock += addUnits;
            dbProd.UpdatedAt = DateTime.UtcNow;

            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Ulid.NewUlid(),
                ProductId = dbProd.Id,
                ProductName = dbProd.Name,
                Type = MovementType.Restock,
                QuantityDelta = addUnits,
                StockAfter = dbProd.Stock,
                ReferenceId = $"GRN-{DateTime.Now:yyyyMMdd}",
                PerformedBy = CurrentUser?.Name ?? "Admin",
                Reason = $"Quick Restock (+{addUnits})",
                CreatedAt = DateTime.UtcNow
            });

            db.SaveChanges();
            LoadDatabaseData();
            StatusMessage = $"Restocked +{addUnits} units of '{dbProd.Name}' (New stock: {dbProd.Stock}).";
        }
    }

    private void CreateStaffFromAdmin()
    {
        if (string.IsNullOrWhiteSpace(NewStaffUsername) || string.IsNullOrWhiteSpace(NewStaffName) || string.IsNullOrWhiteSpace(NewStaffPin))
        {
            StatusMessage = "Staff Username, Full Name, and PIN are required.";
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var norm = NewStaffUsername.Trim().ToLowerInvariant();
        if (db.Cashiers.Any(c => c.Username.ToLower() == norm))
        {
            StatusMessage = $"Username '{NewStaffUsername}' already exists.";
            return;
        }

        var staff = new Cashier
        {
            Id = Ulid.NewUlid(),
            Username = NewStaffUsername.Trim(),
            Name = NewStaffName.Trim(),
            PinHash = PasswordHasher.HashPin(NewStaffPin.Trim()),
            Role = NewStaffRole,
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        };
        db.Cashiers.Add(staff);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ulid.NewUlid(),
            TerminalId = TerminalName,
            ActionType = "StaffCreated",
            Description = $"Created staff account '{staff.Username}' ({staff.Role})",
            PerformedById = CurrentUser?.Id,
            PerformedByName = CurrentUser?.Name ?? "Admin",
            AuthorizedById = CurrentUser?.Id,
            AuthorizedByName = CurrentUser?.Name ?? "Admin",
            ReferenceId = staff.Username,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
        StatusMessage = $"Created staff account '{NewStaffUsername}' ({NewStaffRole}).";
        NewStaffUsername = string.Empty;
        NewStaffName = string.Empty;
        NewStaffPin = string.Empty;
        LoadDatabaseData();
    }

    private void ToggleTaxInclusiveMode()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var tax = db.TaxRates.FirstOrDefault(t => t.IsDefault) ?? db.TaxRates.FirstOrDefault();
        if (tax != null)
        {
            tax.IsInclusive = !tax.IsInclusive;
            tax.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
            _activeTaxRate = tax;
            _cartEngine.RecalculateCart(CurrentSale, _activeTaxRate);
            RefreshCart();
            StatusMessage = $"Tax mode switched to: {_activeTaxRate.DisplayLabel}.";
        }
    }

    public void SetInventoryPolicy(InventorySellPolicy newPolicy)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var setting = db.StoreSettings.FirstOrDefault(s => s.Key == StoreSetting.InventoryPolicyKey);
        if (setting == null)
        {
            setting = new StoreSetting
            {
                Id = Ulid.NewUlid(),
                Key = StoreSetting.InventoryPolicyKey,
                Value = newPolicy.ToString(),
                UpdatedAt = DateTime.UtcNow
            };
            db.StoreSettings.Add(setting);
        }
        else
        {
            setting.Value = newPolicy.ToString();
            setting.UpdatedAt = DateTime.UtcNow;
        }

        db.AuditLogs.Add(new AuditLog
        {
            Id = Ulid.NewUlid(),
            TerminalId = TerminalName,
            ActionType = "InventoryPolicyChange",
            Description = $"Changed inventory sell policy to {newPolicy}",
            PerformedById = CurrentUser?.Id,
            PerformedByName = CurrentUser?.Name ?? "Admin",
            AuthorizedById = CurrentUser?.Id,
            AuthorizedByName = CurrentUser?.Name ?? "Admin",
            ReferenceId = StoreSetting.InventoryPolicyKey,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
        ActiveInventoryPolicy = newPolicy;
        LoadDatabaseData();
        StatusMessage = $"Updated zero-stock inventory policy to '{InventoryPolicyButtonText}'.";
    }

    private void CycleInventoryPolicy()
    {
        var next = ActiveInventoryPolicy switch
        {
            InventorySellPolicy.BlockOnZeroStock => InventorySellPolicy.WarnAndRequireManagerOverride,
            InventorySellPolicy.WarnAndRequireManagerOverride => InventorySellPolicy.AllowNegativeStock,
            _ => InventorySellPolicy.BlockOnZeroStock
        };
        SetInventoryPolicy(next);
    }

    public void Dispose()
    {
        _scannerListener.BarcodeScanned -= ScannerListener_BarcodeScanned;
        if (OutboxSyncService.Instance != null)
        {
            OutboxSyncService.Instance.SyncStateChanged -= OnSyncStateChanged;
        }
    }
}
