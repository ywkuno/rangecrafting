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

        // Inventory source options.
        public bool allowAllContainers = false;

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
    }
}
