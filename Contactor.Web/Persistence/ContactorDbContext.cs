using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Contactor.Vm201;

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

    public DbSet<SchedulerState> SchedulerStates => Set<SchedulerState>();

    public DbSet<BoardSetting> BoardSettings => Set<BoardSetting>();

    public DbSet<RelayGroup> RelayGroups => Set<RelayGroup>();

    public DbSet<RelayGroupMember> RelayGroupMembers => Set<RelayGroupMember>();

    public DbSet<GroupRuntime> GroupRuntimes => Set<GroupRuntime>();

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
        modelBuilder.Entity<SchedulerState>(ConfigureSchedulerState);
        modelBuilder.Entity<BoardSetting>(ConfigureBoardSetting);
        modelBuilder.Entity<RelayGroup>(ConfigureRelayGroup);
        modelBuilder.Entity<RelayGroupMember>(ConfigureRelayGroupMember);
        modelBuilder.Entity<GroupRuntime>(ConfigureGroupRuntime);
    }

    private static void ConfigureRelayGroup(EntityTypeBuilder<RelayGroup> group)
    {
        group.ToTable("RelayGroup");
        group.Property(g => g.Name).HasMaxLength(RelayGroup.NameMaxLength);
        group.Property(g => g.Icon).HasMaxLength(GroupIcons.MaxLength);
        group.HasMany(g => g.Members).WithOne(m => m.Group).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureRelayGroupMember(EntityTypeBuilder<RelayGroupMember> member)
    {
        member.ToTable("RelayGroupMember", t =>
        {
            t.HasCheckConstraint("CK_RelayGroupMember_Position", $"\"Position\" BETWEEN 1 AND {Vm201Channel.Count}");
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
        relay.ToTable("Relay", t => t.HasCheckConstraint("CK_Relay_Id", $"\"Id\" BETWEEN 1 AND {Vm201Channel.Count}"));
        relay.Property(r => r.Id).ValueGeneratedNever();
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

        // Not a foreign key: relays are never deleted, and an event outlives any change to its relay.
        entry.HasIndex(e => e.TimestampUtc);
        entry.HasIndex(e => new { e.RelayId, e.TimestampUtc });
        entry.HasIndex(e => new { e.GroupId, e.TimestampUtc });
    }

    private static void ConfigureRelayRuntime(EntityTypeBuilder<RelayRuntime> runtime)
    {
        runtime.ToTable("RelayRuntime");
        runtime.HasKey(r => r.RelayId);
        runtime.Property(r => r.RelayId).ValueGeneratedNever();
        runtime.Property(r => r.TurnedOnBy).HasMaxLength(User.EmailMaxLength);
        runtime.HasOne<Relay>().WithOne().HasForeignKey<RelayRuntime>(r => r.RelayId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSchedulerState(EntityTypeBuilder<SchedulerState> state)
    {
        state.ToTable("SchedulerState", t => t.HasCheckConstraint("CK_SchedulerState_Id", $"\"Id\" = {SchedulerState.SingletonId}"));
        state.Property(s => s.Id).ValueGeneratedNever();
    }

    private static void ConfigureBoardSetting(EntityTypeBuilder<BoardSetting> setting)
    {
        setting.ToTable("BoardSetting", t =>
        {
            t.HasCheckConstraint("CK_BoardSetting_Id", $"\"Id\" = {BoardSetting.SingletonId}");
            t.HasCheckConstraint("CK_BoardSetting_Port", "\"Port\" BETWEEN 1 AND 65535");
        });
        setting.Property(s => s.Id).ValueGeneratedNever();
        setting.Property(s => s.Host).HasMaxLength(BoardSetting.HostMaxLength);
        setting.Property(s => s.Username).HasMaxLength(Vm201Command.CredentialMaxLength);
        setting.Property(s => s.Password).HasMaxLength(Vm201Command.CredentialMaxLength);
    }

    private static void ConfigureInput(EntityTypeBuilder<Input> input)
    {
        input.ToTable("Input", t => t.HasCheckConstraint("CK_Input_Id", $"\"Id\" = {Input.SingletonId}"));
        input.Property(i => i.Id).ValueGeneratedNever();
        input.Property(i => i.Name).HasMaxLength(Input.TextMaxLength);
        input.Property(i => i.ClosedText).HasMaxLength(Input.TextMaxLength);
        input.Property(i => i.OpenText).HasMaxLength(Input.TextMaxLength);
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
