#nullable disable
namespace CompanyUtilityApp.UserControls
{
    partial class AreaControl
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            btnEdit = new Button();
            dgvAreas = new DataGridView();
            label1 = new Label();
            cmbRoute = new ComboBox();
            panel1 = new Panel();
            panel2 = new Panel();
            ((System.ComponentModel.ISupportInitialize)dgvAreas).BeginInit();
            panel1.SuspendLayout();
            panel2.SuspendLayout();
            SuspendLayout();
            // 
            // btnEdit
            // 
            btnEdit.Location = new Point(156, 94);
            btnEdit.Name = "btnEdit";
            btnEdit.Size = new Size(94, 29);
            btnEdit.TabIndex = 0;
            btnEdit.Text = "EDIT";
            btnEdit.UseVisualStyleBackColor = true;
            btnEdit.Click += btnEdit_Click;
            // 
            // dgvAreas
            // 
            dgvAreas.AllowUserToAddRows = false;
            dgvAreas.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dgvAreas.Dock = DockStyle.Fill;
            dgvAreas.Location = new Point(0, 0);
            dgvAreas.Name = "dgvAreas";
            dgvAreas.ReadOnly = true;
            dgvAreas.RowHeadersWidth = 51;
            dgvAreas.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvAreas.Size = new Size(986, 324);
            dgvAreas.TabIndex = 2;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(51, 49);
            label1.Name = "label1";
            label1.Size = new Size(99, 20);
            label1.TabIndex = 3;
            label1.Text = "Select Route: ";
            // 
            // cmbRoute
            // 
            cmbRoute.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRoute.FormattingEnabled = true;
            cmbRoute.Location = new Point(156, 46);
            cmbRoute.Name = "cmbRoute";
            cmbRoute.Size = new Size(151, 28);
            cmbRoute.TabIndex = 4;
            cmbRoute.SelectedIndexChanged += cmbRoute_SelectedIndexChanged;
            // 
            // panel1
            // 
            panel1.Controls.Add(cmbRoute);
            panel1.Controls.Add(btnEdit);
            panel1.Controls.Add(label1);
            panel1.Dock = DockStyle.Top;
            panel1.Location = new Point(0, 0);
            panel1.Name = "panel1";
            panel1.Size = new Size(986, 156);
            panel1.TabIndex = 5;
            // 
            // panel2
            // 
            panel2.Controls.Add(dgvAreas);
            panel2.Dock = DockStyle.Fill;
            panel2.Location = new Point(0, 156);
            panel2.Name = "panel2";
            panel2.Size = new Size(986, 324);
            panel2.TabIndex = 6;
            // 
            // AreaControl
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(panel2);
            Controls.Add(panel1);
            Name = "AreaControl";
            Size = new Size(986, 480);
            ((System.ComponentModel.ISupportInitialize)dgvAreas).EndInit();
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            panel2.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private Button btnEdit;
        private DataGridView dgvAreas;
        private Label label1;
        private ComboBox cmbRoute;
        private Panel panel1;
        private Panel panel2;
    }
}
