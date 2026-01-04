namespace EventSwift.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class AddLapseDateToEvent : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.Events", "LapseDate", c => c.DateTime());
            DropColumn("dbo.EventProposals", "LapseDate");
        }
        
        public override void Down()
        {
            AddColumn("dbo.EventProposals", "LapseDate", c => c.DateTime());
            DropColumn("dbo.Events", "LapseDate");
        }
    }
}
