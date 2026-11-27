using SQLite;

namespace personalHub.Server.Data
{
    [Table("goal_steps")]
    public class GoalStepRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("goal_id")]
        public string? GoalId { get; set; }

        [Column("title")]
        public string? Title { get; set; }

        [Column("status")]
        public int Status { get; set; }

        [Column("piece_code")]
        public string? PieceCode { get; set; }

        [Column("estimate_min")]
        public int EstimateMin { get; set; }

        [Column("source_idea_id")]
        public string? SourceIdeaId { get; set; }

        [Column("deadline")]
        public long Deadline { get; set; }

        [Column("reason")]
        public string? Reason { get; set; }

        [Column("artifact_uri")]
        public string? ArtifactUri { get; set; }

        [Column("completed_at")]
        public long CompletedAt { get; set; }

        [Column("sort_order")]
        public int SortOrder { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        [Column("deleted")]
        public int Deleted { get; set; }
    }

}
