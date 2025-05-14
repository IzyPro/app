﻿using System.Text.Json;
using BTCPayApp.Core.Auth;
using BTCPayApp.Core.Data;
using BTCPayApp.Core.LDK;
using BTCPayApp.Core.Wallet;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;

namespace BTCPayApp.Core.Helpers;

public static class StoreHelpers
{
    public static async Task<(GenericPaymentMethodData? onchain, GenericPaymentMethodData? lightning)>
        GetCurrentStorePaymentMethods(this IAccountManager accountManager)
    {
        var storeId = accountManager.CurrentStore?.Id;
        var pms = await accountManager.GetClient().GetStorePaymentMethods(storeId, includeConfig: true);
        var onchain = pms.FirstOrDefault(pm => pm.PaymentMethodId == OnChainWalletManager.PaymentMethodId);
        var lightning = pms.FirstOrDefault(pm => pm.PaymentMethodId == LightningNodeManager.PaymentMethodId);
        return (onchain, lightning);
    }

    public static async Task<(GenericPaymentMethodData? onchain, GenericPaymentMethodData? lightning)?> TryApplyingAppPaymentMethodsToCurrentStore(
         this IAccountManager accountManager,
         OnChainWalletManager onChainWalletManager, LightningNodeManager lightningNodeService, bool applyOnchain, bool applyLighting)
    {
        var storeId = accountManager.CurrentStore?.Id;
        var userId = accountManager.UserInfo?.UserId;
        var config = await onChainWalletManager.GetConfig();
        if (// are user and store present?
            string.IsNullOrEmpty(userId) ||
            string.IsNullOrEmpty(storeId) ||
            // is user permitted? (store owner)
            !await accountManager.IsAuthorized(Policies.CanModifyStoreSettings, storeId) ||
            // is the onchain wallet configured?
            !OnChainWalletManager.IsConfigured(config)) return null;
        // check the store's payment methods
        var (onchain, lightning) = await GetCurrentStorePaymentMethods(accountManager);

        // onchain
        if (applyOnchain && config?.Derivations.TryGetValue(WalletDerivation.NativeSegwit, out var derivation) is true && onchain is null)
        {
            onchain = await accountManager.GetClient().UpdateStorePaymentMethod(storeId, OnChainWalletManager.PaymentMethodId, new UpdatePaymentMethodRequest
            {
                Enabled = true,
                Config = derivation.Descriptor
            });
        }

        // lightning
        if (applyLighting && lightning is null && lightningNodeService is { IsActive: true, Node.ApiKeyManager: { } apiKeyManager })
        {
            var key = await apiKeyManager.GetKeyForStore(storeId, APIKeyPermission.Write);
            lightning = await accountManager.GetClient().UpdateStorePaymentMethod(storeId,
                LightningNodeManager.PaymentMethodId, new UpdatePaymentMethodRequest
                {
                    Enabled = true,
                    Config = key.ConnectionString(userId)
                });
        }

        return (onchain, lightning);
    }

    public static async Task<bool> IsOnChainOurs(this OnChainWalletManager onChainWalletManager, GenericPaymentMethodData? onchain)
    {
        if (!string.IsNullOrEmpty(onchain?.Config.ToString()))
        {
            var config = await onChainWalletManager.GetConfig();
            using var jsonDoc = JsonDocument.Parse(onchain.Config.ToString());
            if (jsonDoc.RootElement.TryGetProperty("accountDerivation", out var derivationSchemeElement) &&
                derivationSchemeElement.GetString() is { } derivationScheme &&
                config?.Derivations.Any(pair => pair.Value.Identifier == $"DERIVATIONSCHEME:{derivationScheme}") is true)
                return true;
        }

        return false;
    }

    public static async Task<bool> IsLightningOurs(this LightningNodeManager lightningNodeManager, GenericPaymentMethodData? lightning)
    {
        if (!string.IsNullOrEmpty(lightning?.Config.ToString()))
        {
            var node = lightningNodeManager.Node;
            var apiKeyManager = node?.ApiKeyManager;
            if (apiKeyManager == null) return false;
            using var jsonDoc = JsonDocument.Parse(lightning.Config.ToString());
            if (jsonDoc.RootElement.TryGetProperty("connectionString", out var connectionStringElement) &&
                connectionStringElement.GetString() is { } connectionString &&
                LightningConnectionStringHelper.ExtractValues(connectionString, out var lnConnectionString) is { } lnValues &&
                lnConnectionString == "app" && lnValues.TryGetValue("key", out var key) && key is not null &&
                await node!.ApiKeyManager.CheckPermission(key, APIKeyPermission.Read))
                return true;
        }

        return false;
    }
}
