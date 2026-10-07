using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Common;

namespace MasryVoice.Api.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();
    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<PendingBooking> PendingBookings => Set<PendingBooking>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<OutboxJob> OutboxJobs => Set<OutboxJob>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Agent configuration
        modelBuilder.Entity<Agent>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Name).IsRequired().HasMaxLength(150);
            b.Property(a => a.LanguageCode).HasMaxLength(10);
            b.Property(a => a.ModelName).HasMaxLength(100);
        });

        // Conversation configuration
        modelBuilder.Entity<Conversation>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.Agent)
             .WithMany()
             .HasForeignKey(c => c.AgentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // Message configuration
        modelBuilder.Entity<Message>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasOne(m => m.Conversation)
             .WithMany(c => c.Messages)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);

            // Fast sequential seek per conversation for chat history
            b.HasIndex(m => new { m.ConversationId, m.SequenceNumber });
        });

        // ToolExecution configuration
        modelBuilder.Entity<ToolExecution>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.Conversation)
             .WithMany(c => c.ToolExecutions)
             .HasForeignKey(t => t.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);

            // Fast lookup for conversation audit trail
            b.HasIndex(t => new { t.ConversationId, t.ExecutedAtUtc });
        });

        // AvailabilitySlot configuration
        modelBuilder.Entity<AvailabilitySlot>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.ServiceName).IsRequired().HasMaxLength(150);
            
            // Fast range seek for slot availability by date/time
            b.HasIndex(s => s.StartTimeUtc);

            // Check constraint for capacity protection in PostgreSQL
            b.ToTable(t => t.HasCheckConstraint("CK_AvailabilitySlots_Capacity", "\"BookedCapacity\" <= \"TotalCapacity\""));
        });

        // PendingBooking configuration
        modelBuilder.Entity<PendingBooking>(b =>
        {
            b.HasKey(pb => pb.Id);
            b.HasOne(pb => pb.Conversation)
             .WithMany(c => c.PendingBookings)
             .HasForeignKey(pb => pb.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(pb => pb.Slot)
             .WithMany(s => s.PendingBookings)
             .HasForeignKey(pb => pb.SlotId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(pb => new { pb.ConversationId, pb.Status });
            b.HasIndex(pb => pb.CreatedAtUtc);
        });

        // Booking configuration
        modelBuilder.Entity<Booking>(b =>
        {
            b.HasKey(bk => bk.Id);
            b.HasOne(bk => bk.Slot)
             .WithMany(s => s.Bookings)
             .HasForeignKey(bk => bk.SlotId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne<Conversation>()
             .WithMany()
             .HasForeignKey(bk => bk.ConversationId)
             .OnDelete(DeleteBehavior.SetNull);

            // Unique index on IdempotencyKey to prevent duplicate booking creation
            b.HasIndex(bk => bk.IdempotencyKey).IsUnique();
            // Fast order-by-descending seek for recent bookings
            b.HasIndex(bk => bk.CreatedAtUtc);
            b.HasIndex(bk => bk.ConversationId);
        });

        if (Database.IsNpgsql())
        {
            modelBuilder.HasPostgresExtension("vector");
        }

        // KnowledgeDocument configuration
        modelBuilder.Entity<KnowledgeDocument>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.Title).IsRequired().HasMaxLength(250);
            b.Property(d => d.Category).HasMaxLength(100);
            b.HasIndex(d => d.CreatedAtUtc);
        });

        // DocumentChunk configuration
        modelBuilder.Entity<DocumentChunk>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.Document)
             .WithMany(d => d.Chunks)
             .HasForeignKey(c => c.DocumentId)
             .OnDelete(DeleteBehavior.Cascade);

            if (Database.IsNpgsql())
            {
                b.Property(c => c.Embedding).HasColumnType("vector(384)");
            }
            else
            {
                b.Ignore(c => c.Embedding);
            }

            b.HasIndex(c => c.DocumentId);
        });

        // OutboxJob configuration
        modelBuilder.Entity<OutboxJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.Topic).IsRequired().HasMaxLength(100);
            b.Property(j => j.Status).IsRequired().HasMaxLength(50);
            b.HasIndex(j => new { j.Status, j.NextRetryUtc });
        });
    }

    public async Task SeedInitialDataAsync()
    {
        await EnsureTablesCreatedAsync();

        // 1. Seed or Update Default Egyptian Arabic Agent
        var defaultAgent = await Agents.FirstOrDefaultAsync(a => a.Id == Guid.Parse("11111111-1111-1111-1111-111111111111"));
        if (defaultAgent == null)
        {
            defaultAgent = new Agent
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "سارة - مساعدة عيادة النور التخصصية",
                LanguageCode = "ar-EG",
                ModelName = "qwen2.5:3b",
                Temperature = 0.2,
                IsActive = true,
                AllowedToolsJson = "[\"CheckAvailability\",\"StageBooking\",\"GetBooking\",\"SearchKnowledgeBase\"]",
                SystemPrompt = """
                أنتِ سارة، مساعدة عيادة النور التخصصية في القاهرة. تتحدثين بالعامية المصرية الودودة.
                تعليمات استخدام الأدوات:
                1. عند سؤال المريض عن المواعيد فقط، استدعي أداة CheckAvailability.
                2. عندما يطلب المريض الحجز أو يذكر اسمه وتليفونه (مثال: احجزلي باسم فلان وتليفوني كذا)، استدعي فوراً وحصراً أداة StageBooking بالبيانات: customerName و customerPhone، ولا تستدعي CheckAvailability في هذه الحالة.
                3. بعد استدعاء StageBooking، اطلبي من العميل مراجعة التفاصيل والضغط على زر 'تأكيد الحجز' في الشاشة.
                4. لا تقومي بتأكيد الحجز بنفسك، فالتأكيد يتم حصرياً عبر ضغط العميل على زر التأكيد.
                5. عندما يسأل العميل عن الخدمات والأسعار، استدعي أداة SearchKnowledgeBase.
                """
            };

            Agents.Add(defaultAgent);
            await SaveChangesAsync();
        }

        // Seed initial Clinic Knowledge Document if none exist
        if (!await KnowledgeDocuments.AnyAsync())
        {
            var clinicDoc = new KnowledgeDocument
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Title = "دليل عيادة النور التخصصية - الخدمات والأسعار",
                FileName = "clinic_guide.md",
                Category = "ServicesAndPricing",
                ChunkCount = 2,
                CreatedAtUtc = DateTime.UtcNow
            };

            clinicDoc.Chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = clinicDoc.Id,
                ChunkIndex = 0,
                Content = "عيادة النور التخصصية تقع في شارع التحرير بالدقي، الجيزة. تخصصات العيادة: عيادة الأسنان، عيادة الباطنة، وعيادة الأطفال. مواعيد العمل من الأحد للخميس من 9 صباحاً حتى 5 مساءً بتوقيت القاهرة.",
                CreatedAtUtc = DateTime.UtcNow
            });

            clinicDoc.Chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = clinicDoc.Id,
                ChunkIndex = 1,
                Content = "أسعار الكشوفات في عيادة النور: كشف الباطنة العام 300 جنيه مصري، كشف الأسنان 350 جنيه مصري يشمل الفحص والأشعة الأولية، كشف الأطفال 250 جنيه مصري. الاستشارة مجانية خلال 14 يوماً من تاريخ الكشف.",
                CreatedAtUtc = DateTime.UtcNow
            });

            KnowledgeDocuments.Add(clinicDoc);
            await SaveChangesAsync();
        }

        // 2. Populate embeddings for unindexed chunks in PostgreSQL
        if (Database.IsNpgsql())
        {
            var unindexedChunks = await DocumentChunks.Where(c => c.Embedding == null).ToListAsync();
            if (unindexedChunks.Count > 0)
            {
                var embedder = new MasryVoice.Api.Features.Knowledge.DeterministicEmbeddingProvider();
                foreach (var chunk in unindexedChunks)
                {
                    var floats = await embedder.GenerateEmbeddingAsync(chunk.Content);
                    chunk.Embedding = new Pgvector.Vector(floats);
                }
                await SaveChangesAsync();
            }
        }

        // 2. Seed availability slots for upcoming Cairo business days
        if (!await AvailabilitySlots.AnyAsync())
        {
            var todayCairo = CairoTimeHelper.NowCairo.Date;
            
            for (int dayOffset = 1; dayOffset <= 3; dayOffset++)
            {
                var targetDate = todayCairo.AddDays(dayOffset);
                if (targetDate.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday)
                {
                    continue;
                }

                int[] hours = [10, 11, 13, 14, 16];
                int[] minutes = [0, 30, 0, 30, 0];

                for (int i = 0; i < hours.Length; i++)
                {
                    var cairoSlotTime = targetDate.AddHours(hours[i]).AddMinutes(minutes[i]);
                    var utcSlotTime = CairoTimeHelper.CairoToUtc(cairoSlotTime);

                    AvailabilitySlots.Add(new AvailabilitySlot
                    {
                        Id = Guid.NewGuid(),
                        ServiceName = "كشف باطنة عامة",
                        StartTimeUtc = utcSlotTime,
                        EndTimeUtc = utcSlotTime.AddMinutes(30),
                        TotalCapacity = 1,
                        BookedCapacity = 0
                    });
                }
            }

            await SaveChangesAsync();
        }
    }

    public async Task EnsureTablesCreatedAsync()
    {
        if (Database.IsNpgsql())
        {
            await Database.ExecuteSqlRawAsync("""
                CREATE EXTENSION IF NOT EXISTS vector;

                CREATE TABLE IF NOT EXISTS "KnowledgeDocuments" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "Title" character varying(250) NOT NULL,
                    "FileName" text NOT NULL,
                    "Category" character varying(100) NOT NULL,
                    "ChunkCount" integer NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "DocumentChunks" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "DocumentId" uuid NOT NULL REFERENCES "KnowledgeDocuments"("Id") ON DELETE CASCADE,
                    "ChunkIndex" integer NOT NULL,
                    "Content" text NOT NULL,
                    "Embedding" vector(384),
                    "CreatedAtUtc" timestamp with time zone NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "OutboxJobs" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "Topic" character varying(100) NOT NULL,
                    "PayloadJson" text NOT NULL,
                    "Status" character varying(50) NOT NULL,
                    "RetryCount" integer NOT NULL,
                    "MaxRetries" integer NOT NULL,
                    "NextRetryUtc" timestamp with time zone NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "ProcessedAtUtc" timestamp with time zone,
                    "LastError" text
                );

                CREATE INDEX IF NOT EXISTS "IX_KnowledgeDocuments_CreatedAtUtc" ON "KnowledgeDocuments" ("CreatedAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_DocumentChunks_DocumentId" ON "DocumentChunks" ("DocumentId");
                CREATE INDEX IF NOT EXISTS "IX_OutboxJobs_Status_NextRetryUtc" ON "OutboxJobs" ("Status", "NextRetryUtc");
            """);
        }
        else if (Database.IsSqlite())
        {
            await Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "KnowledgeDocuments" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Title" TEXT NOT NULL,
                    "FileName" TEXT NOT NULL,
                    "Category" TEXT NOT NULL,
                    "ChunkCount" INTEGER NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "DocumentChunks" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "DocumentId" TEXT NOT NULL REFERENCES "KnowledgeDocuments"("Id") ON DELETE CASCADE,
                    "ChunkIndex" INTEGER NOT NULL,
                    "Content" TEXT NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "OutboxJobs" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Topic" TEXT NOT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "RetryCount" INTEGER NOT NULL,
                    "MaxRetries" INTEGER NOT NULL,
                    "NextRetryUtc" TEXT NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "ProcessedAtUtc" TEXT,
                    "LastError" TEXT
                );
            """);
        }
    }
}
