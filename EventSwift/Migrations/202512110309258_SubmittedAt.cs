namespace EventSwift.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class SubmittedAt : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.ProposalFeedbacks", "SubmittedAt", c => c.DateTime(nullable: false));
            AddColumn("dbo.ProposalFeedbacks", "FeedbackMessage", c => c.String());
        }
        
        public override void Down()
        {
            DropColumn("dbo.ProposalFeedbacks", "FeedbackMessage");
            DropColumn("dbo.ProposalFeedbacks", "SubmittedAt");
        }
    }
}
