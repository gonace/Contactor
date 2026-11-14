using Contactor.Boards;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Contactor.Web.Persistence;

/// <summary>
/// The app's SQLite database. Times are stored as UTC; schedule times as local "HH:mm".
/// </summary>
public sealed class ContactorDbContext : DbContext
{
    public ContactorDbContext(DbContextOptions<ContactorDbContext> options)
        : base(options)
    {
    }

    public DbSet<Relay> Relays => Set<Relay>();

    public DbSet<Schedule> Schedules => Set<Schedule>();

    public DbSet<User> Users => Set<User>();

    public DbSet<EventLogEntry> EventLog => Set<EventLogEntry>();

    public DbSet<RelayRuntime> RelayRuntimes => Set<RelayRuntime>();

    public DbSet<Input> Inputs => Set<Input>();

    public DbSet<Board> Boards => Set<Board>();

    public DbSet<BoardRuntime> BoardRuntimes => Set<BoardRuntime>();

    public DbSet<RelayGroup> RelayGroups => Set<RelayGroup>();

    public DbSet<RelayGroupMember> RelayGroupMembers => Set<RelayGroupMember>();

    public DbSet<GroupRuntime> GroupRuntimes => Set<GroupRuntime>();

    public DbSet<Installation> Installations => Set<Installation>();

    public DbSet<InputAction> InputActions => Set<InputAction>();

    public DbSet<CloudflareSetting> CloudflareSettings => Set<CloudflareSetting>();

    public DbSet<TailscaleSetting> TailscaleSettings => Set<TailscaleSetting>();

    /// <summary>
    /// The board, while the app runs one (several at once is issue #4): the first in the list, or null before one is
    /// chosen.
    /// </summary>
    public Task<Board?> FirstBoardAsync(CancellationToken cancellationToken)
    {
        return Boards.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The id of <see cref="FirstBoardAsync"/>'s board, e.g. for an event about it; null before one is chosen.</summary>
    public Task<int?> FirstBoardIdAsync(CancellationToken cancellationToken)
    {
        return Boards.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The runtime row of <see cref="FirstBoardAsync"/>'s board, tracked, added when it has none yet; null before a board
    /// is chosen.
    /// </summary>
    public async Task<BoardRuntime?> FirstBoardRuntimeAsync(CancellationToken cancellationToken)
    {
        if (await FirstBoardIdAsync(cancellationToken) is not { } boardId)
        {
            return null;
        }

        var runtime = await BoardRuntimes.SingleOrDefaultAsync(r => r.BoardId == boardId, cancellationToken);
        if (runtime is null)
        {
            runtime = new BoardRuntime { BoardId = boardId };
            BoardRuntimes.Add(runtime);
        }

        return runtime;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite has no time zones: every DateTime is UTC, and comes back marked as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<TimeOnly>().HaveConversion<HourMinuteConverter>().HaveMaxLength(5);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Relay>(ConfigureRelay);
        modelBuilder.Entity<Schedule>(ConfigureSchedule);
        modelBuilder.Entity<User>(ConfigureUser);
        modelBuilder.Entity<EventLogEntry>(ConfigureEventLog);
        modelBuilder.Entity<RelayRuntime>(ConfigureRelayRuntime);
        modelBuilder.Entity<Input>(ConfigureInput);
        modelBuilder.Entity<Board>(ConfigureBoard);
        modelBuilder.Entity<BoardRuntime>(ConfigureBoardRuntime);
        modelBuilder.Entity<RelayGroup>(ConfigureRelayGroup);
        modelBuilder.Entity<RelayGroupMember>(ConfigureRelayGroupMember);
        modelBuilder.Entity<GroupRuntime>(ConfigureGroupRuntime);
        modelBuilder.Entity<Installation>(ConfigureInstallation);
        modelBuilder.Entity<InputAction>(ConfigureInputAction);
        modelBuilder.Entity<CloudflareSetting>(ConfigureCloudflareSetting);
        modelBuilder.Entity<TailscaleSetting>(ConfigureTailscaleSetting);
    }

    private static void ConfigureRelayGroup(EntityTypeBuilder<RelayGroup> group)
    {
        group.ToTable("RelayGroup");
        group.Property(g => g.Name).HasMaxLength(RelayGroup.NameMaxLength);
        group.Property(g => g.Icon).HasMaxLength(GroupIcons.MaxLength);
        group.HasOne<Board>().WithMany().HasForeignKey(g => g.BoardId).OnDelete(DeleteBehavior.Cascade);
        group.HasMany(g => g.Members).WithOne(m => m.Group).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureRelayGroupMember(EntityTypeBuilder<RelayGroupMember> member)
    {
        member.ToTable("RelayGroupMember", t =>
        {
            t.HasCheckConstraint("CK_RelayGroupMember_Position", $"\"Position\" BETWEEN 1 AND {ChannelMask.MaxChannels}");
            t.HasCheckConstraint(
                "CK_RelayGroupMember_StartDelaySec",
                $"\"StartDelaySec\" BETWEEN {RelayGroupMember.MinStartDelaySec} AND {RelayGroupMember.MaxStartDelaySec}");
            t.HasCheckConstraint("CK_RelayGroupMember_RunOnSec", $"\"RunOnSec\" BETWEEN 0 AND {RelayGroupMember.MaxRunOnSec}");
        });
        member.HasKey(m => new { m.GroupId, m.RelayId });
        member.Property(m => m.RoleName).HasMaxLength(RelayGroupMember.RoleNameMaxLength);

        // A relay is in at most one group, and a group has one relay per step.
        member.HasIndex(m => m.RelayId).IsUnique();
        member.HasIndex(m => new { m.GroupId, m.Position }).IsUnique();
        member.HasOne<Relay>().WithMany().HasForeignKey(m => m.RelayId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureGroupRuntime(EntityTypeBuilder<GroupRuntime> runtime)
    {
        runtime.ToTable("GroupRuntime");
        runtime.HasKey(r => r.GroupId);
        runtime.Property(r => r.GroupId).ValueGeneratedNever();
        runtime.Property(r => r.Phase).HasMaxLength(GroupPhases.MaxLength);
        runtime.Property(r => r.TurnedOnBy).HasMaxLength(User.EmailMaxLength);
        runtime.HasOne<RelayGroup>().WithOne().HasForeignKey<GroupRuntime>(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureRelay(EntityTypeBuilder<Relay> relay)
    {
        relay.ToTable("Relay", t =>
        {
            t.HasCheckConstraint("CK_Relay_Id", $"\"Id\" BETWEEN 1 AND {ChannelMask.MaxChannels}");
            t.HasCheckConstraint("CK_Relay_Channel", $"\"Channel\" BETWEEN 1 AND {ChannelMask.MaxChannels}");
        });
        relay.Property(r => r.Id).ValueGeneratedNever();
        relay.HasIndex(r => new { r.BoardId, r.Channel }).IsUnique();
        relay.HasOne<Board>().WithMany().HasForeignKey(r => r.BoardId).OnDelete(DeleteBehavior.Cascade);
        relay.Property(r => r.Name).HasMaxLength(Relay.NameMaxLength);
        relay.HasMany(r => r.Schedules).WithOne(s => s.Relay).HasForeignKey(s => s.RelayId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSchedule(EntityTypeBuilder<Schedule> schedule)
    {
        schedule.ToTable("Schedule", t =>
        {
            t.HasCheckConstraint("CK_Schedule_Days", $"\"Days\" BETWEEN 1 AND {(int)ScheduleDays.All}");

            // A schedule switches a relay or a group, never both and never neither.
            t.HasCheckConstraint("CK_Schedule_Target", "(\"RelayId\" IS NULL) <> (\"GroupId\" IS NULL)");
        });
        schedule.Property(s => s.CreatedBy).HasMaxLength(User.EmailMaxLength);
        schedule.HasIndex(s => s.RelayId);
        schedule.HasIndex(s => s.GroupId);

        // A group's schedules go with the group.
        schedule.HasOne(s => s.Group).WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureUser(EntityTypeBuilder<User> user)
    {
        user.ToTable("User");
        user.HasKey(u => u.Email);
        user.Property(u => u.Email).HasMaxLength(User.EmailMaxLength);
        user.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength);
    }

    private static void ConfigureEventLog(EntityTypeBuilder<EventLogEntry> entry)
    {
        entry.ToTable("EventLog");
        entry.Property(e => e.Actor).HasMaxLength(User.EmailMaxLength);
        entry.Property(e => e.Action).HasMaxLength(EventLogEntry.ActionMaxLength);

        // A board's events go with the board.
        entry.HasOne<Board>().WithMany().HasForeignKey(e => e.BoardId).OnDelete(DeleteBehavior.Cascade);
        entry.HasIndex(e => new { e.BoardId, e.TimestampUtc });

        // Not foreign keys: relays are never deleted, and an event outlives any change to its relay.
        entry.HasIndex(e => e.TimestampUtc);
        entry.HasIndex(e => new { e.RelayId, e.TimestampUtc });
        entry.HasIndex(e => new { e.GroupId, e.TimestampUtc });
        entry.HasIndex(e => new { e.InputId, e.TimestampUtc });
    }

    private static void ConfigureRelayRuntime(EntityTypeBuilder<RelayRuntime> runtime)
    {
        runtime.ToTable("RelayRuntime");
        runtime.HasKey(r => r.RelayId);
        runtime.Property(r => r.RelayId).ValueGeneratedNever();
        runtime.Property(r => r.TurnedOnBy).HasMaxLength(User.EmailMaxLength);
        runtime.Property(r => r.Version).IsConcurrencyToken();
        runtime.HasOne<Relay>().WithOne().HasForeignKey<RelayRuntime>(r => r.RelayId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBoard(EntityTypeBuilder<Board> board)
    {
        board.ToTable("Board", t =>
        {
            t.HasCheckConstraint("CK_Board_Port", "\"Port\" BETWEEN 1 AND 65535");

            // An address set in the app has both; without one, the board's own settings give both.
            t.HasCheckConstraint("CK_Board_Address", "(\"Host\" IS NULL) = (\"Port\" IS NULL)");
        });
        board.Property(b => b.Name).HasMaxLength(Board.NameMaxLength);
        board.Property(b => b.Type).HasMaxLength(Board.TypeMaxLength);
        board.Property(b => b.Host).HasMaxLength(Board.HostMaxLength);
        board.Property(b => b.Username).HasMaxLength(BoardLogin.MaxLength);
        board.Property(b => b.Password).HasMaxLength(BoardLogin.MaxLength);
    }

    private static void ConfigureBoardRuntime(EntityTypeBuilder<BoardRuntime> runtime)
    {
        runtime.ToTable("BoardRuntime");
        runtime.HasKey(r => r.BoardId);
        runtime.Property(r => r.BoardId).ValueGeneratedNever();
        runtime.HasOne<Board>().WithOne().HasForeignKey<BoardRuntime>(r => r.BoardId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureInstallation(EntityTypeBuilder<Installation> installation)
    {
        installation.ToTable("Installation", t => t.HasCheckConstraint("CK_Installation_Id", $"\"Id\" = {Installation.SingletonId}"));
        installation.Property(i => i.Id).ValueGeneratedNever();
        installation.Property(i => i.AccessType).HasMaxLength(Installation.MethodTypeMaxLength);
        installation.Property(i => i.SignInType).HasMaxLength(Installation.MethodTypeMaxLength);
    }

    private static void ConfigureCloudflareSetting(EntityTypeBuilder<CloudflareSetting> setting)
    {
        setting.ToTable("CloudflareSetting", t => t.HasCheckConstraint("CK_CloudflareSetting_Id", $"\"Id\" = {CloudflareSetting.SingletonId}"));
        setting.Property(s => s.Id).ValueGeneratedNever();
        setting.Property(s => s.TunnelToken).HasMaxLength(CloudflareSetting.TunnelTokenMaxLength);
        setting.Property(s => s.Address).HasMaxLength(CloudflareSetting.HostNameMaxLength);
        setting.Property(s => s.TeamDomain).HasMaxLength(CloudflareSetting.HostNameMaxLength);
        setting.Property(s => s.Audience).HasMaxLength(CloudflareSetting.AudienceMaxLength);
    }

    private static void ConfigureTailscaleSetting(EntityTypeBuilder<TailscaleSetting> setting)
    {
        setting.ToTable("TailscaleSetting", t => t.HasCheckConstraint("CK_TailscaleSetting_Id", $"\"Id\" = {TailscaleSetting.SingletonId}"));
        setting.Property(s => s.Id).ValueGeneratedNever();
        setting.Property(s => s.AuthKey).HasMaxLength(TailscaleSetting.AuthKeyMaxLength);
        setting.Property(s => s.HostName).HasMaxLength(TailscaleSetting.HostNameMaxLength);
    }

    private static void ConfigureInput(EntityTypeBuilder<Input> input)
    {
        input.ToTable("Input", t =>
        {
            t.HasCheckConstraint("CK_Input_Id", $"\"Id\" BETWEEN 1 AND {ChannelMask.MaxChannels}");
            t.HasCheckConstraint("CK_Input_Channel", $"\"Channel\" BETWEEN 1 AND {ChannelMask.MaxChannels}");
        });
        input.Property(i => i.Id).ValueGeneratedNever();
        input.HasIndex(i => new { i.BoardId, i.Channel }).IsUnique();
        input.HasOne<Board>().WithMany().HasForeignKey(i => i.BoardId).OnDelete(DeleteBehavior.Cascade);
        input.Property(i => i.Name).HasMaxLength(Input.NameMaxLength);
    }

    private static void ConfigureInputAction(EntityTypeBuilder<InputAction> action)
    {
        action.ToTable("InputAction", t =>
        {
            // An input's action switches a relay or a group, never both and never neither.
            t.HasCheckConstraint("CK_InputAction_Target", "(\"RelayId\" IS NULL) <> (\"GroupId\" IS NULL)");
        });

        // One row per relay or group an input controls.
        action.HasIndex(a => new { a.InputId, a.RelayId }).IsUnique();
        action.HasIndex(a => new { a.InputId, a.GroupId }).IsUnique();
        action.HasIndex(a => a.RelayId);
        action.HasIndex(a => a.GroupId);
        action.HasOne<Input>().WithMany().HasForeignKey(a => a.InputId).OnDelete(DeleteBehavior.Restrict);
        action.HasOne(a => a.Relay).WithMany().HasForeignKey(a => a.RelayId).OnDelete(DeleteBehavior.Restrict);

        // A group's actions go with the group.
        action.HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupId).OnDelete(DeleteBehavior.Cascade);
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(
                value => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(),
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
        {
        }
    }

    private sealed class HourMinuteConverter : ValueConverter<TimeOnly, string>
    {
        public HourMinuteConverter()
            : base(
                value => value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                value => TimeOnly.ParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture))
        {
        }
    }
}
