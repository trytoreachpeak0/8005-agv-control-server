namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// The single-slot controlled business specification (REQ-0257): length, width, height and basket
/// compatibility, and nothing else. No load rating, no owning face -- the face is part of the
/// vehicle model's SlotPosition. One template is referenced by many slots.
/// </summary>
public sealed class SlotTemplateRow
{
    public required string SlotTemplateId { get; set; }
    public required string TemplateKey { get; set; }
    public long Version { get; set; }
    public int LengthMm { get; set; }
    public int WidthMm { get; set; }
    public int HeightMm { get; set; }
    public required string CompatibleBasketTypesJson { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? SnapshotId { get; set; }
}

/// <summary>
/// The whole-vehicle layer (REQ-0257): which physical slots exist, their numbering, their
/// SlotPosition, and which template each one references. Versioned and immutable once published,
/// separately from the templates it references.
/// </summary>
public sealed class SlotModelVersionRow
{
    public required string SlotModelVersionId { get; set; }
    public required string ModelKey { get; set; }
    public long Version { get; set; }
    public required string Status { get; set; }
    public int SlotCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? SnapshotId { get; set; }
}

/// <summary>One physical slot inside a vehicle model version.</summary>
public sealed class SlotModelSlotRow
{
    public required string SlotModelVersionId { get; set; }
    public int PhysicalSlotNumber { get; set; }
    public required string SlotPosition { get; set; }
    public required string SlotTemplateId { get; set; }
}

/// <summary>
/// One slot's IO facts on one vehicle (REQ-0261, REQ-0267): the unlock output, the lock feedback
/// input, the in-slot light curtain input, signal polarity and the pulse reset. Changing any of
/// these is a hardware change and goes through whole-vehicle maintenance, never through a template
/// edit.
/// </summary>
public sealed class SlotIoBindingRow
{
    public required string SlotIoBindingId { get; set; }
    public required string AgvId { get; set; }
    public required string SlotModelVersionId { get; set; }
    public int PhysicalSlotNumber { get; set; }
    public required string UnlockOutputPoint { get; set; }
    public required string LockFeedbackInputPoint { get; set; }
    public required string LightCurtainInputPoint { get; set; }
    public required string SignalPolarity { get; set; }
    public int PulseResetMilliseconds { get; set; }
    public long Version { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? SnapshotId { get; set; }
}

/// <summary>
/// What one vehicle is actually running right now, and the fingerprint the onboard reports back in
/// its CapabilitySnapshot so the server can tell which version is on the car.
/// </summary>
public sealed class ActiveSlotConfigurationRow
{
    public required string AgvId { get; set; }
    public required string SlotModelVersionId { get; set; }
    public long ConfigurationVersion { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public required string ActivationId { get; set; }
    public required string SnapshotId { get; set; }
}

/// <summary>
/// One activation attempt. A rollback is one of these too -- it selects old immutable content and
/// activates it now, which is why there is no "restore history" path anywhere (REQ-0346).
/// </summary>
public sealed class SlotConfigurationActivationRow
{
    public required string ActivationId { get; set; }
    public required string AgvId { get; set; }
    public required string SlotModelVersionId { get; set; }
    public long ConfigurationVersion { get; set; }
    public required string Fingerprint { get; set; }

    /// <summary><c>ACTIVATION</c> or <c>ROLLBACK</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>For a rollback, the version whose content was selected. That version is not modified.</summary>
    public long? RolledBackToVersion { get; set; }

    /// <summary><c>PENDING_RESULT</c> until the onboard result arrives -- never guessed either way.</summary>
    public required string State { get; set; }

    public required string RecoveryRole { get; set; }
    public string? CommandMessageId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ResultReceivedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? SnapshotId { get; set; }
}

/// <summary>
/// One slot's verification on one vehicle (REQ-0263): open, close and in-place signals each
/// confirmed by hand. Sampling is rejected -- a vehicle is verified only when every one of its slots
/// has a row here.
/// </summary>
public sealed class SlotConfigurationVerificationRow
{
    public required string VerificationId { get; set; }
    public required string AgvId { get; set; }
    public required string SlotModelVersionId { get; set; }
    public int PhysicalSlotNumber { get; set; }
    public bool OpenSignalConfirmed { get; set; }
    public bool CloseSignalConfirmed { get; set; }
    public bool InPlaceSignalConfirmed { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public string? FieldRecordReference { get; set; }
}

/// <summary>
/// Per-vehicle readiness (REQ-0259). One vehicle failing this says nothing about any other vehicle:
/// the gate is a per-car judgement, and the three existing cars are brought up one at a time.
/// </summary>
public sealed class SlotConfigurationReadinessRow
{
    public required string AgvId { get; set; }
    public required string SlotModelVersionId { get; set; }
    public bool Ready { get; set; }
    public required string ReasonCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}
