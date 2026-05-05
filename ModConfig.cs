namespace ClaimLinkedCrafting
{
    public class ModConfig
    {
        // Master switch.
        public bool modEnabled = true;

        // claimOnly (default): only consume from storages in active claim context
        // rangeOnly: fallback mode using configured range radius
        // disabled: emergency no-op mode
        public ClaimMode mode = ClaimMode.ClaimOnly;

        // Search scope for both claim and range logic.
        public float range = 20f;

        // Claim radius (default 20 around LCB center in vanilla footprints).
        public float claimRadius = 20f;

        // Extra search radius for claim block lookup; <= 0 means auto.
        public float claimSearchRadius = 0f;

        // Refresh intervals (frames) to avoid expensive scans every frame.
        public int scanCooldownFrames = 6;
        public int claimRefreshCooldownFrames = 10;

        // claimOnly recovery and safety toggles.
        public bool claimOnlyAllowRangeFallback = true;
        public bool requireOwnerMetadataMatch = false;

        // Permission model used when resolving claim ownership.
        // - permitClaimOwner: standard owner match.
        // - permitClaimFriend: allow friend list entries.
        // - permitClaimAlly: allow ally-style entries when present.
        // - permitClaimParty: allow party teammates when available in claim metadata.
        // - permitClaimClan: allow clan/tf/tribe style entries when available.
        // Presets: ownerOnly, friendsOnly, allies, trustedOnly, vanilla, custom
        public string permissionProfile = "vanilla";
        public bool permitClaimOwner = true;
        public bool permitClaimFriend = true;
        public bool permitClaimAlly = true;
        public bool permitClaimParty = false;
        public bool permitClaimClan = false;

        // Storage container targeting.
        // Empty list means no whitelist filtering.
        public string[] allowedStorageContainerNames = new string[0];
        // If a block/container name contains any blocked token, it is skipped.
        public string[] blockedStorageContainerNames = new string[0];

        // Inventory source options.
        public bool allowAllContainers = false;

        // Optional explicit confirmation required before storage extraction is allowed.
        public bool requireStorageUseConfirmation = false;
        // Temporary confirmation lifetime in seconds when `/rconfirm` is used.
        public float storageUseConfirmationSeconds = 30f;

        // Optional broader patch for edge versions.
        public bool patchHasItems = false;

        // Claim block blockName keywords.
        public string[] landClaimBlockNames = new[] { "landclaim", "claimblock", "claim block", "deed" };

        // Logging / debug.
        public bool isDebug = false;

        // Search + highlight feature.
        public bool enableRangeSearch = true;
        public int searchMaxResults = 8;
        public float searchRange = 0f;
        public bool highlightSearchResults = true;
        public int highlightMarkerLimit = 4;
        public float searchMarkerDuration = 30f;
        public int searchCommandCooldownFrames = 10;

        // Storage drain order used when consuming ingredients from linked containers.
        // nearest (default), farthest, name, quantity
        public string storageConsumptionOrder = "nearest";
    }
}
