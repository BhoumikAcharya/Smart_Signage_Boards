#nullable disable
namespace CompanyUtilityApp.Forms
{
    partial class AddEditNodeForm
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

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            label1 = new Label();
            label2 = new Label();
            label3 = new Label();
            txtRoute = new TextBox();
            txtIPAddress = new TextBox();
            cmbPanelLocation = new ComboBox();
            label4 = new Label();
            txtDescription = new TextBox();
            btnSave = new Button();
            btnCancel = new Button();
            chkCalibration = new CheckBox();
            label5 = new Label();
            cmbPanelSerialNumber = new ComboBox();
            label6 = new Label();
            txtHR = new TextBox();
            txtNodeNumber = new TextBox();
            label7 = new Label();
            SuspendLayout();
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(32, 45);
            label1.Name = "label1";
            label1.Size = new Size(55, 20);
            label1.TabIndex = 0;
            label1.Text = "Route: ";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(422, 45);
            label2.Name = "label2";
            label2.Size = new Size(85, 20);
            label2.TabIndex = 1;
            label2.Text = "IP Address: ";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(209, 45);
            label3.Name = "label3";
            label3.Size = new Size(112, 20);
            label3.TabIndex = 2;
            label3.Text = "Panel Location: ";
            // 
            // txtRoute
            // 
            txtRoute.Enabled = false;
            txtRoute.Location = new Point(32, 68);
            txtRoute.Name = "txtRoute";
            txtRoute.Size = new Size(125, 27);
            txtRoute.TabIndex = 0;
            // 
            // txtIPAddress
            // 
            txtIPAddress.Location = new Point(422, 68);
            txtIPAddress.Name = "txtIPAddress";
            txtIPAddress.Size = new Size(125, 27);
            txtIPAddress.TabIndex = 4;
            // 
            // cmbPanelLocation
            // 
            cmbPanelLocation.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cmbPanelLocation.AutoCompleteSource = AutoCompleteSource.ListItems;
            cmbPanelLocation.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPanelLocation.FormattingEnabled = true;
            cmbPanelLocation.Location = new Point(209, 68);
            cmbPanelLocation.Name = "cmbPanelLocation";
            cmbPanelLocation.Size = new Size(151, 28);
            cmbPanelLocation.TabIndex = 1;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(422, 197);
            label4.Name = "label4";
            label4.Size = new Size(158, 20);
            label4.TabIndex = 6;
            label4.Text = "Description (optional):";
            // 
            // txtDescription
            // 
            txtDescription.Location = new Point(422, 220);
            txtDescription.Multiline = true;
            txtDescription.Name = "txtDescription";
            txtDescription.Size = new Size(421, 49);
            txtDescription.TabIndex = 6;
            // 
            // btnSave
            // 
            btnSave.Location = new Point(72, 294);
            btnSave.Name = "btnSave";
            btnSave.Size = new Size(94, 29);
            btnSave.TabIndex = 8;
            btnSave.Text = "Save";
            btnSave.UseVisualStyleBackColor = true;
            btnSave.Click += btnSave_Click;
            // 
            // btnCancel
            // 
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(209, 294);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(94, 29);
            btnCancel.TabIndex = 9;
            btnCancel.Text = "Cancel";
            btnCancel.UseVisualStyleBackColor = true;
            // 
            // chkCalibration
            // 
            chkCalibration.AutoSize = true;
            chkCalibration.Location = new Point(32, 119);
            chkCalibration.Name = "chkCalibration";
            chkCalibration.Size = new Size(144, 24);
            chkCalibration.TabIndex = 7;
            chkCalibration.Text = "Calibration Done";
            chkCalibration.UseVisualStyleBackColor = true;
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(209, 119);
            label5.Name = "label5";
            label5.Size = new Size(146, 20);
            label5.TabIndex = 11;
            label5.Text = "Panel Serial Number:";
            // 
            // cmbPanelSerialNumber
            // 
            cmbPanelSerialNumber.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cmbPanelSerialNumber.AutoCompleteSource = AutoCompleteSource.ListItems;
            cmbPanelSerialNumber.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPanelSerialNumber.FormattingEnabled = true;
            cmbPanelSerialNumber.Location = new Point(209, 142);
            cmbPanelSerialNumber.Name = "cmbPanelSerialNumber";
            cmbPanelSerialNumber.Size = new Size(151, 28);
            cmbPanelSerialNumber.TabIndex = 2;
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(209, 197);
            label6.Name = "label6";
            label6.Size = new Size(124, 20);
            label6.TabIndex = 13;
            label6.Text = "Holding Register:";
            // 
            // txtHR
            // 
            txtHR.Enabled = false;
            txtHR.Location = new Point(209, 220);
            txtHR.Name = "txtHR";
            txtHR.Size = new Size(125, 27);
            txtHR.TabIndex = 3;
            // 
            // txtNodeNumber
            // 
            txtNodeNumber.Location = new Point(422, 143);
            txtNodeNumber.Name = "txtNodeNumber";
            txtNodeNumber.Size = new Size(125, 27);
            txtNodeNumber.TabIndex = 5;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Location = new Point(422, 120);
            label7.Name = "label7";
            label7.Size = new Size(107, 20);
            label7.TabIndex = 15;
            label7.Text = "Node Number:";
            // 
            // AddEditNodeForm
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(882, 355);
            Controls.Add(txtNodeNumber);
            Controls.Add(label7);
            Controls.Add(txtHR);
            Controls.Add(label6);
            Controls.Add(cmbPanelSerialNumber);
            Controls.Add(label5);
            Controls.Add(chkCalibration);
            Controls.Add(btnCancel);
            Controls.Add(btnSave);
            Controls.Add(txtDescription);
            Controls.Add(label4);
            Controls.Add(cmbPanelLocation);
            Controls.Add(txtIPAddress);
            Controls.Add(txtRoute);
            Controls.Add(label3);
            Controls.Add(label2);
            Controls.Add(label1);
            Name = "AddEditNodeForm";
            Text = "AddEditNodeForm";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label label1;
        private Label label2;
        private Label label3;
        private TextBox txtRoute;
        private TextBox txtIPAddress;
        private ComboBox cmbPanelLocation;
        private Label label4;
        private TextBox txtDescription;
        private Button btnSave;
        private Button btnCancel;
        private CheckBox chkCalibration;
        private Label label5;
        private ComboBox cmbPanelSerialNumber;
        private Label label6;
        private TextBox txtHR;
        private TextBox txtNodeNumber;
        private Label label7;
    }
}