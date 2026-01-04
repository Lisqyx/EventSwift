namespace EventSwift.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class AddLapseDateToEventProposal : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.EventProposals", "LapseDate", c => c.DateTime());
        }
        
        public override void Down()
        {
            DropColumn("dbo.EventProposals", "LapseDate");
        }
    }
}
