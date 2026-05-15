using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

#pragma warning disable CS0618, CS8765

namespace OneRoubleMarket;

public sealed record MetaData : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "Milkkira.OneRoubleMarket";
    public override string Name { get; init; } = "One Rouble Market";
    public override string Author { get; init; } = "Milkkira";
    public override List<string> Contributors { get; init; } = [];
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new(">=4.0.0");
    public override List<string> Incompatibilities { get; init; } = [];
    public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = [];
    public override string Url { get; init; } = "https://github.com/MilkKira/OneRoubleMarket";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

[Injectable(InjectionType.Singleton, TypePriority = 450000)]
public sealed class OneRoubleMarketPlugin : IOnLoad
{
    private const double ForcedPrice = 1D;
    private const string RoubleTpl = "5449016a4bdc2d6f028b456f";
    private const string DollarTpl = "5696686a4bdc2da3298b456a";
    private const string EuroTpl = "569668774bdc2da2298b4568";

    private readonly DatabaseService databaseService;
    private readonly ConfigServer configServer;
    private readonly ISptLogger<OneRoubleMarketPlugin> logger;

    public OneRoubleMarketPlugin(
        DatabaseService databaseService,
        ConfigServer configServer,
        ISptLogger<OneRoubleMarketPlugin> logger)
    {
        this.databaseService = databaseService;
        this.configServer = configServer;
        this.logger = logger;
    }

    public Task OnLoad()
    {
        var tables = databaseService.GetTables();

        var templatePrices = ForceTemplatePrices(tables.Templates.Prices);
        var handbookPrices = ForceHandbookPrices(tables.Templates.Handbook);
        var traderOffers = ForceTraderAssorts(tables.Traders);
        var clothingOffers = ForceSuitRequirements(tables.Traders);
        var serviceOffers = ForceTraderServiceCosts(tables.Traders);

        ForceRagfairConfig();
        ForceTraderConfig();

        logger.Success(
            $"OneRoubleMarket: forced {traderOffers} trader offers, {templatePrices} template prices, " +
            $"{handbookPrices} handbook prices, {clothingOffers} clothing offers, and {serviceOffers} trader services to 1 rouble.",
            null);

        return Task.CompletedTask;
    }

    private static int ForceTemplatePrices(Dictionary<MongoId, double> prices)
    {
        var count = 0;

        foreach (var tpl in prices.Keys.ToList())
        {
            prices[tpl] = ForcedPrice;
            count++;
        }

        return count;
    }

    private static int ForceHandbookPrices(HandbookBase handbook)
    {
        var count = 0;

        foreach (var item in handbook.Items)
        {
            item.Price = ForcedPrice;
            count++;
        }

        return count;
    }

    private static int ForceTraderAssorts(Dictionary<MongoId, Trader> traders)
    {
        var count = 0;

        foreach (var trader in traders.Values)
        {
            var barterSchemes = trader.Assort?.BarterScheme;
            if (barterSchemes is null)
            {
                continue;
            }

            foreach (var offerSchemes in barterSchemes.Values)
            {
                foreach (var requirementGroup in offerSchemes)
                {
                    requirementGroup.Clear();
                    requirementGroup.Add(CreateRoubleBarterScheme());
                }

                count++;
            }
        }

        return count;
    }

    private static int ForceSuitRequirements(Dictionary<MongoId, Trader> traders)
    {
        var count = 0;

        foreach (var trader in traders.Values)
        {
            if (trader.Suits is null)
            {
                continue;
            }

            foreach (var suit in trader.Suits)
            {
                var itemRequirements = suit.Requirements?.ItemRequirements;
                if (itemRequirements is null)
                {
                    continue;
                }

                itemRequirements.Clear();
                itemRequirements.Add(
                    new ItemRequirement
                    {
                        Count = ForcedPrice,
                        Tpl = RoubleTpl,
                        OnlyFunctional = false,
                    });
                count++;
            }
        }

        return count;
    }

    private static int ForceTraderServiceCosts(Dictionary<MongoId, Trader> traders)
    {
        var count = 0;

        foreach (var trader in traders.Values)
        {
            if (trader.Services is null)
            {
                continue;
            }

            foreach (var service in trader.Services)
            {
                if (service.ItemsToPay is null)
                {
                    continue;
                }

                service.ItemsToPay.Clear();
                service.ItemsToPay[RoubleTpl] = 1;
                count++;
            }
        }

        return count;
    }

    private void ForceRagfairConfig()
    {
        var ragfairConfig = configServer.GetConfig<RagfairConfig>();
        var dynamicConfig = ragfairConfig.Dynamic;
        if (dynamicConfig is null)
        {
            return;
        }

        if (dynamicConfig.Barter is not null)
        {
            dynamicConfig.Barter.ChancePercent = 0D;
            dynamicConfig.Barter.MinRoubleCostToBecomeBarter = double.MaxValue;
        }

        dynamicConfig.UseTraderPriceForOffersIfHigher = false;

        dynamicConfig.ItemPriceMultiplier?.Clear();
        dynamicConfig.ItemPriceOverrideRouble?.Clear();

        if (dynamicConfig.OfferCurrencyChangePercent is not null)
        {
            dynamicConfig.OfferCurrencyChangePercent[RoubleTpl] = 100D;
            dynamicConfig.OfferCurrencyChangePercent[DollarTpl] = 0D;
            dynamicConfig.OfferCurrencyChangePercent[EuroTpl] = 0D;
        }

        if (dynamicConfig.PriceRanges is not null)
        {
            ForcePriceRange(dynamicConfig.PriceRanges.Default);
            ForcePriceRange(dynamicConfig.PriceRanges.Preset);
            ForcePriceRange(dynamicConfig.PriceRanges.Pack);
        }

        if (dynamicConfig.GenerateBaseFleaPrices is not null)
        {
            dynamicConfig.GenerateBaseFleaPrices.PriceMultiplier = ForcedPrice;
            dynamicConfig.GenerateBaseFleaPrices.HideoutCraftMultiplier = ForcedPrice;
            dynamicConfig.GenerateBaseFleaPrices.PreventPriceBeingBelowTraderBuyPrice = false;
            dynamicConfig.GenerateBaseFleaPrices.ItemTplMultiplierOverride?.Clear();
            dynamicConfig.GenerateBaseFleaPrices.ItemTypeMultiplierOverride?.Clear();
        }
    }

    private void ForceTraderConfig()
    {
        var traderConfig = configServer.GetConfig<TraderConfig>();
        traderConfig.TraderPriceMultiplier = ForcedPrice;
    }

    private static void ForcePriceRange(MinMax<double> range)
    {
        range.Min = ForcedPrice;
        range.Max = ForcedPrice;
    }

    private static BarterScheme CreateRoubleBarterScheme()
    {
        return new BarterScheme
        {
            Count = ForcedPrice,
            Template = RoubleTpl,
            OnlyFunctional = false,
        };
    }
}
